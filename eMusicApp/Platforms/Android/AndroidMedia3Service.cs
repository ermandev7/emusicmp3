using Android.App;
using Android.Content;
using Android.Graphics;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.DataSource.Cache;
using AndroidX.Media3.Database;
using AndroidX.Media3.Session;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace eMusicApp.Platforms.Android
{
    [Service(Exported = true, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback)]
    [IntentFilter(new[] { "androidx.media3.session.MediaLibraryService", "android.media.browse.MediaBrowserService" })]
    public class AndroidMedia3Service : MediaLibraryService
    {
        private MediaLibraryService.MediaLibrarySession? _mediaSession;
        private IExoPlayer? _player;

        public static AndroidMedia3Service? Instance { get; private set; }

        private global::Android.OS.Handler? _progressHandler;
        private global::Java.Lang.Runnable? _progressRunnable;

        // Cola nativa para autoplay infinito
        private Queue<string> _nativeQueue = new Queue<string>();
        private bool _isFetchingNext = false;
        private bool _nextPrepared = false;
        private string? _currentMediaId;
        private string? _currentArtist;
        private string? _currentTitle;
        private bool _trackEndedReported = false;

        // IDs ya reproducidos para evitar loops en radio mode
        private readonly HashSet<string> _playedIds = new HashSet<string>();

        // Crossfade
        private bool _isFadingIn;

        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = System.TimeSpan.FromSeconds(30)
        };

        // Caché LRU 500 MB
        private SimpleCache? _simpleCache;
        private StandaloneDatabaseProvider? _databaseProvider;

        private const string CHANNEL_ID     = "emusic_playback";
        private const int    NOTIFICATION_ID = 1001;

        // Acciones para botones de la notificación
        private const string ACTION_PREV       = "emusic.ACTION_PREV";
        private const string ACTION_PLAY_PAUSE = "emusic.ACTION_PLAY_PAUSE";
        private const string ACTION_NEXT       = "emusic.ACTION_NEXT";

        // Artwork cacheado para la notificación
        private Bitmap? _artworkBitmap;
        private string? _artworkUrl;

        public override void OnCreate()
        {
            base.OnCreate();
            Instance = this;

            CreateNotificationChannel();

            // ── Puente MAUI → Nativo ──
            NativeAudioController.OnPlayRequested = (url, title, artist, thumb, videoId) =>
                PlayStream(url, title, artist, thumb, videoId);
            NativeAudioController.OnPauseRequested  = Pause;
            NativeAudioController.OnResumeRequested = Resume;
            NativeAudioController.OnSeekRequested   = pos => SeekTo(pos);
            NativeAudioController.OnUpdateQueueRequested = videoIds =>
            {
                _nativeQueue.Clear();
                foreach (var id in videoIds) _nativeQueue.Enqueue(id);
                _nextPrepared = false;
            };

            // ── Caché LRU 500 MB ──
            var cacheDir = new Java.IO.File(CacheDir, "emusic_audio_cache");
            _databaseProvider = new StandaloneDatabaseProvider(this);
            _simpleCache = new SimpleCache(
                cacheDir,
                new LeastRecentlyUsedCacheEvictor(500 * 1024 * 1024),
                _databaseProvider);

            var cacheDataSourceFactory = new CacheDataSource.Factory()
                .SetCache(_simpleCache)
                .SetUpstreamDataSourceFactory(
                    new DefaultHttpDataSource.Factory().SetAllowCrossProtocolRedirects(true))
                .SetFlags(CacheDataSource.FlagIgnoreCacheOnError);

            var audioAttributes = new AndroidX.Media3.Common.AudioAttributes.Builder()
                .SetUsage(C.UsageMedia)
                .SetContentType(C.AudioContentTypeMusic)
                .Build();

            _player = new ExoPlayerBuilder(this)
                .SetMediaSourceFactory(new DefaultMediaSourceFactory(this).SetDataSourceFactory(cacheDataSourceFactory))
                .SetWakeMode(C.WakeModeNetwork)
                .SetHandleAudioBecomingNoisy(true)
                .SetAudioAttributes(audioAttributes, true)
                .Build();

            _mediaSession = new MediaLibraryService.MediaLibrarySession.Builder(this, _player, new LibrarySessionCallback())
                .Build();

            _progressHandler  = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
            _progressRunnable = new global::Java.Lang.Runnable(OnProgressTick);
            _progressHandler.PostDelayed(_progressRunnable, 500);

            // ── Promover a foreground con notificación MediaStyle manual ──
            PostMediaNotification();
        }

        public override MediaLibraryService.MediaLibrarySession? OnGetSessionFromMediaLibraryService(MediaSession.ControllerInfo? controllerInfo)
            => _mediaSession;

        // ── Notificación MediaStyle manual (Plan B) ──
        // Suprimir la notificación automática de Media3 y usar la nuestra propia.
        public override void OnUpdateNotification(MediaSession session, bool startInForegroundRequired)
        {
            // NO llamar a base ni a PostMediaNotification aquí.
            // PostMediaNotification ya llama StartForeground, que re-dispara OnUpdateNotification.
            // Dejar vacío para romper el ciclo.
        }

        public override StartCommandResult OnStartCommand(Intent? intent, global::Android.App.StartCommandFlags flags, int startId)
        {
            if (intent?.Action != null)
            {
                switch (intent.Action)
                {
                    case ACTION_PREV:
                        if (_player != null && _player.HasPreviousMediaItem)
                            _player.SeekToPreviousMediaItem();
                        else
                            NativeAudioController.OnSkipToPrevious?.Invoke();
                        break;
                    case ACTION_PLAY_PAUSE:
                        if (_player != null)
                        {
                            if (_player.IsPlaying) _player.Pause();
                            else _player.Play();
                        }
                        break;
                    case ACTION_NEXT:
                        if (_player != null && _player.HasNextMediaItem)
                            _player.SeekToNextMediaItem();
                        else
                            NativeAudioController.OnSkipToNext?.Invoke();
                        break;
                }
                PostMediaNotification();
            }
            return base.OnStartCommand(intent, flags, startId);
        }

        private PendingIntent BuildActionPendingIntent(string action)
        {
            var intent = new Intent(this, Java.Lang.Class.FromType(typeof(AndroidMedia3Service)));
            intent.SetAction(action);
            return PendingIntent.GetForegroundService(this, action.GetHashCode(), intent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
        }

        private bool _isPostingNotification;
        private void PostMediaNotification()
        {
            if (_isPostingNotification) return; // Guard contra recursión de StartForeground → OnUpdateNotification
            _isPostingNotification = true;
            try {
            bool isPlaying = _player?.IsPlaying ?? false;
            string title  = _player?.CurrentMediaItem?.MediaMetadata?.Title?.ToString()  ?? "eMusicApp";
            string artist = _player?.CurrentMediaItem?.MediaMetadata?.Artist?.ToString() ?? "";

            var builder = new Notification.Builder(this, CHANNEL_ID)
                .SetContentTitle(title)
                .SetContentText(artist)
                .SetSmallIcon(Resource.Mipmap.appicon)
                .SetOngoing(isPlaying)
                .SetVisibility(NotificationVisibility.Public)
                .AddAction(new Notification.Action.Builder(
                    global::Android.Resource.Drawable.IcMediaPrevious, "Anterior",
                    BuildActionPendingIntent(ACTION_PREV)).Build())
                .AddAction(new Notification.Action.Builder(
                    isPlaying ? global::Android.Resource.Drawable.IcMediaPause
                              : global::Android.Resource.Drawable.IcMediaPlay,
                    isPlaying ? "Pausa" : "Play",
                    BuildActionPendingIntent(ACTION_PLAY_PAUSE)).Build())
                .AddAction(new Notification.Action.Builder(
                    global::Android.Resource.Drawable.IcMediaNext, "Siguiente",
                    BuildActionPendingIntent(ACTION_NEXT)).Build());

            // MediaStyle con token de sesión para lock screen + burbuja
            var style = new Notification.MediaStyle()
                .SetShowActionsInCompactView(0, 1, 2);
            try
            {
                if (_mediaSession != null)
                {
                    var token = _mediaSession.PlatformToken;
                    if (token is global::Android.Media.Session.MediaSession.Token platformToken)
                        style.SetMediaSession(platformToken);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Notification] Error getting platform token: {ex.Message}");
            }
            builder.SetStyle(style);

            if (_artworkBitmap != null)
                builder.SetLargeIcon(_artworkBitmap);

            // Abrir la app al tocar la notificación
            var launchIntent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "");
            if (launchIntent != null)
            {
                var contentIntent = PendingIntent.GetActivity(this, 0, launchIntent,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);
                builder.SetContentIntent(contentIntent);
            }

            var notification = builder.Build();

            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.Q)
                StartForeground(NOTIFICATION_ID, notification,
                    global::Android.Content.PM.ForegroundService.TypeMediaPlayback);
            else
                StartForeground(NOTIFICATION_ID, notification);
            }
            finally { _isPostingNotification = false; }
        }

        private async Task LoadArtworkAsync(string? url)
        {
            if (string.IsNullOrEmpty(url) || url == _artworkUrl) return;
            _artworkUrl = url;
            try
            {
                var bytes = await _httpClient.GetByteArrayAsync(url);
                _artworkBitmap = BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                PostMediaNotification(); // Actualizar notificación con artwork
            }
            catch { }
        }

        // ── Tick de progreso — reporta a PlayerViewModel cada 500 ms ──
        private void OnProgressTick()
        {
            _progressHandler?.PostDelayed(_progressRunnable!, 500);
            if (_player == null) return;

            var state        = _player.PlaybackState;
            bool isBuffering = state == 2; // STATE_BUFFERING
            bool isPlaying   = _player.IsPlaying;
            bool isEnded     = state == 4; // STATE_ENDED

            NativeAudioController.ReportBufferingState(isBuffering);
            if (!isBuffering)
                NativeAudioController.ReportPlaybackState(isPlaying);

            if (isEnded && !_trackEndedReported)
            {
                _trackEndedReported = true;
                NativeAudioController.ReportTrackEnded();

                // Safety net: si MAUI está suspendido (pantalla apagada), el nativo
                // se encarga de buscar la siguiente canción y reproducirla.
                if (!_isFetchingNext && !string.IsNullOrEmpty(_currentMediaId))
                    _ = AutoContinuePlaybackAsync();
            }
            else if (!isEnded)
            {
                _trackEndedReported = false;
            }

            if (_player.CurrentMediaItem != null)
            {
                long dur  = _player.Duration;
                long pos  = _player.CurrentPosition;
                int durMs = dur < 0 ? 0 : (int)dur;
                int posMs = pos < 0 ? 0 : (int)pos;

                NativeAudioController.ReportProgress(posMs, durMs);

                // Crossfade: bajar volumen al final del track
                int xfMs = NativeAudioController.CrossfadeDurationMs;
                if (xfMs > 0 && durMs > 2000 && !_isFadingIn)
                {
                    int remaining = durMs - posMs;
                    if (remaining < xfMs && remaining >= 0)
                        _player.Volume = Math.Max(0f, (float)remaining / xfMs);
                    else if (_player.Volume < 0.99f)
                        _player.Volume = 1f;
                }

                // Detectar cambio de track (auto-avance nativo de ExoPlayer)
                var playingId = _player.CurrentMediaItem?.MediaId;
                if (!string.IsNullOrEmpty(playingId) && playingId != _currentMediaId)
                {
                    _currentMediaId = playingId;
                    _playedIds.Add(playingId);
                    var title  = _player.CurrentMediaItem?.MediaMetadata?.Title?.ToString()      ?? "";
                    var artist = _player.CurrentMediaItem?.MediaMetadata?.Artist?.ToString()     ?? "";
                    _currentArtist = artist;
                    _currentTitle = title;
                    var thumb  = _player.CurrentMediaItem?.MediaMetadata?.ArtworkUri?.ToString() ?? "";
                    NativeAudioController.ReportTrackStarted(playingId, title, artist, thumb, durMs);
                    PostMediaNotification();
                    _ = LoadArtworkAsync(thumb);

                    _nextPrepared = false;
                    _trackEndedReported = false;

                    if (xfMs > 0)
                    {
                        _player.Volume = 0f;
                        _ = FadeInAsync(xfMs);
                    }
                    else
                    {
                        _player.Volume = 1f;
                    }

                    if (_nativeQueue.Count > 0 && !_isFetchingNext)
                        _ = FetchNextTrackNativelyAsync();
                }
            }

            if (_player.PlayerError != null)
            {
                NativeAudioController.ReportPlaybackState(false);
                NativeAudioController.ReportTrackEnded();
                _player.ClearMediaItems();
            }
        }

        private async Task FadeInAsync(int durationMs)
        {
            _isFadingIn = true;
            try
            {
                const int steps = 20;
                int delay = Math.Max(20, durationMs / steps);
                for (int i = 1; i <= steps; i++)
                {
                    if (_player == null) break;
                    _player.Volume = (float)i / steps;
                    await Task.Delay(delay);
                }
                if (_player != null) _player.Volume = 1f;
            }
            finally { _isFadingIn = false; }
        }

        // ── Reproducir ──
        public void PlayStream(string url, string title, string artist, string thumbUrl, string videoId)
        {
            if (_player == null) return;

            _nextPrepared = false;
            _trackEndedReported = false;
            _isFadingIn = false;
            _nativeQueue.Clear();
            _player.Volume = 1f;
            _currentArtist = artist;
            _playedIds.Add(videoId);

            _player.ClearMediaItems();
            _player.SetMediaItem(CreateMediaItem(url, title, artist, thumbUrl, videoId));
            _player.Prepare();
            _player.Play();

            PostMediaNotification();
            _ = LoadArtworkAsync(thumbUrl);

            _ = FetchRelatedAndQueueNextAsync(videoId);
        }

        private async Task FetchRelatedAndQueueNextAsync(string videoId)
        {
            try
            {
                _nativeQueue.Clear();

                // Tier 1: relatedStreams
                {
                    var response = await _httpClient.GetStringAsync(
                        $"{AppConstants.ApiBaseUrl}/streams/{videoId}");
                    using var doc = JsonDocument.Parse(response);
                    var root = doc.RootElement;

                    if (root.TryGetProperty("relatedStreams", out var related))
                    {
                        foreach (var rel in related.EnumerateArray())
                        {
                            var vId = ExtractVideoId(rel);
                            if (!string.IsNullOrEmpty(vId) && vId != videoId
                                && !_playedIds.Contains(vId)
                                && !_nativeQueue.Contains(vId))
                                _nativeQueue.Enqueue(vId);
                        }
                    }
                }

                // Tier 2: búsqueda por artista
                if (_nativeQueue.Count == 0 && !string.IsNullOrEmpty(_currentArtist))
                    await EnqueueFromArtistSearchAsync(_currentArtist);

                System.Diagnostics.Debug.WriteLine($"[Autoplay] Cola: {_nativeQueue.Count} canciones");

                if (_nativeQueue.Count > 0 && !_isFetchingNext)
                    await FetchNextTrackNativelyAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Autoplay] Error related: {ex.Message}");
            }
        }

        /// <summary>
        /// Busca canciones por nombre de artista y las añade a la cola nativa,
        /// filtrando las ya reproducidas. Fallback cuando relatedStreams se agota.
        /// </summary>
        private async Task EnqueueFromArtistSearchAsync(string artist)
        {
            try
            {
                var searchUrl = $"{AppConstants.ApiBaseUrl}/search?q={Uri.EscapeDataString(artist)}";
                var response = await _httpClient.GetStringAsync(searchUrl);
                using var doc = JsonDocument.Parse(response);

                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        // Solo streams (no playlists ni canales)
                        if (item.TryGetProperty("type", out var typeEl))
                        {
                            var type = typeEl.GetString();
                            if (!string.IsNullOrEmpty(type) && type != "stream") continue;
                        }

                        var vId = ExtractVideoId(item);
                        if (!string.IsNullOrEmpty(vId) && !_playedIds.Contains(vId))
                            _nativeQueue.Enqueue(vId);

                        if (_nativeQueue.Count >= 10) break;
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[Autoplay] Búsqueda artista '{artist}': {_nativeQueue.Count} tracks nuevos");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Autoplay] Error búsqueda artista: {ex.Message}");
            }
        }

        // Lee videoId de "videoId" directamente, o lo extrae del campo "url" (/watch?v=XXX)
        private static string? ExtractVideoId(JsonElement el)
        {
            if (el.TryGetProperty("videoId", out var idEl))
            {
                var v = idEl.GetString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
            if (el.TryGetProperty("url", out var urlEl))
            {
                var url = urlEl.GetString() ?? "";
                var idx = url.IndexOf("?v=");
                if (idx >= 0) return url.Substring(idx + 3);
            }
            return null;
        }

        public async Task FetchNextTrackNativelyAsync()
        {
            if (_isFetchingNext || _nextPrepared || _nativeQueue.Count == 0) return;
            _isFetchingNext = true;
            try
            {
                var nextVideoId = _nativeQueue.Dequeue();
                var response = await _httpClient.GetStringAsync(
                    $"{AppConstants.ApiBaseUrl}/streams/{nextVideoId}");
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                var title    = root.GetProperty("title").GetString()        ?? "Unknown";
                var uploader = root.GetProperty("uploader").GetString()     ?? "";
                var thumb    = root.GetProperty("thumbnailUrl").GetString() ?? "";

                string? bestUrl     = null;
                int     bestBitrate = 0;
                foreach (var s in root.GetProperty("audioStreams").EnumerateArray())
                {
                    int    br  = s.TryGetProperty("bitrate", out var brEl) ? brEl.GetInt32()  : 0;
                    string? su = s.TryGetProperty("url",     out var suEl) ? suEl.GetString() : null;
                    if (su != null && br > bestBitrate) { bestBitrate = br; bestUrl = su; }
                }

                if (!string.IsNullOrEmpty(bestUrl) && _player != null)
                {
                    _player.AddMediaItem(CreateMediaItem(bestUrl, title, uploader, thumb, nextVideoId));
                    _nextPrepared = true;
                    System.Diagnostics.Debug.WriteLine($"[Autoplay] Siguiente pre-cargado: {title}");

                    // Si ExoPlayer ya terminó (STATE_ENDED), la pre-carga llegó tarde.
                    // Hay que re-iniciar la reproducción manualmente.
                    if (_player.PlaybackState == 4) // STATE_ENDED
                    {
                        _player.SeekToNextMediaItem();
                        _player.Prepare();
                        _player.Play();
                        System.Diagnostics.Debug.WriteLine($"[Autoplay] Re-iniciando desde ENDED: {title}");
                    }

                    // Rellenar cola con related filtrando ya reproducidos
                    _nativeQueue.Clear();
                    if (root.TryGetProperty("relatedStreams", out var related))
                    {
                        foreach (var rel in related.EnumerateArray())
                        {
                            var vId = ExtractVideoId(rel);
                            if (!string.IsNullOrEmpty(vId) && !_playedIds.Contains(vId))
                                _nativeQueue.Enqueue(vId);
                        }
                    }

                    // Fallback: buscar por artista si related se agotó
                    if (_nativeQueue.Count == 0 && !string.IsNullOrEmpty(uploader))
                    {
                        _currentArtist = uploader;
                        await EnqueueFromArtistSearchAsync(uploader);
                    }

                    // Último recurso: limpiar historial y reintentar
                    if (_nativeQueue.Count == 0)
                    {
                        _playedIds.Clear();
                        if (root.TryGetProperty("relatedStreams", out var rel2))
                        {
                            foreach (var rel in rel2.EnumerateArray())
                            {
                                var vId = ExtractVideoId(rel);
                                if (!string.IsNullOrEmpty(vId)) _nativeQueue.Enqueue(vId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Autoplay] Error: {ex.Message}");
                _nextPrepared = false;
            }
            finally { _isFetchingNext = false; }
        }

        /// <summary>
        /// Safety net para pantalla apagada: si MAUI no responde al TrackEnded,
        /// el servicio nativo busca related streams y continúa la reproducción.
        /// </summary>
        private async Task AutoContinuePlaybackAsync()
        {
            // Esperar 2s para dar oportunidad a MAUI de responder primero
            await Task.Delay(2000);

            // Si MAUI ya arrancó otra canción, no hacer nada
            if (_player == null || _player.PlaybackState != 4) return; // 4 = STATE_ENDED

            _isFetchingNext = true;
            try
            {
                // Rellenar la cola nativa si está vacía
                if (_nativeQueue.Count == 0 && !string.IsNullOrEmpty(_currentMediaId))
                {
                    // Tier 1: relatedStreams
                    {
                        var response = await _httpClient.GetStringAsync(
                            $"{AppConstants.ApiBaseUrl}/streams/{_currentMediaId}");
                        using var doc = JsonDocument.Parse(response);
                        if (doc.RootElement.TryGetProperty("relatedStreams", out var related))
                        {
                            foreach (var rel in related.EnumerateArray())
                            {
                                var vId = ExtractVideoId(rel);
                                if (!string.IsNullOrEmpty(vId) && vId != _currentMediaId
                                    && !_playedIds.Contains(vId) && !_nativeQueue.Contains(vId))
                                    _nativeQueue.Enqueue(vId);
                            }
                        }
                    }

                    // Tier 2: buscar por artista
                    if (_nativeQueue.Count == 0 && !string.IsNullOrEmpty(_currentArtist))
                        await EnqueueFromArtistSearchAsync(_currentArtist);

                    // Tier 3: limpiar historial
                    if (_nativeQueue.Count == 0)
                    {
                        _playedIds.Clear();
                        var fallbackResp = await _httpClient.GetStringAsync(
                            $"{AppConstants.ApiBaseUrl}/streams/{_currentMediaId}");
                        using var doc2 = JsonDocument.Parse(fallbackResp);
                        if (doc2.RootElement.TryGetProperty("relatedStreams", out var rel2))
                        {
                            foreach (var rel in rel2.EnumerateArray())
                            {
                                var vId = ExtractVideoId(rel);
                                if (!string.IsNullOrEmpty(vId) && vId != _currentMediaId)
                                    _nativeQueue.Enqueue(vId);
                            }
                        }
                    }
                }

                if (_nativeQueue.Count == 0) return;

                var nextVideoId = _nativeQueue.Dequeue();
                var resp = await _httpClient.GetStringAsync(
                    $"{AppConstants.ApiBaseUrl}/streams/{nextVideoId}");
                using var nextDoc = JsonDocument.Parse(resp);
                var root = nextDoc.RootElement;

                var title    = root.GetProperty("title").GetString()        ?? "Unknown";
                var uploader = root.GetProperty("uploader").GetString()     ?? "";
                var thumb    = root.GetProperty("thumbnailUrl").GetString() ?? "";

                string? bestUrl     = null;
                int     bestBitrate = 0;
                foreach (var s in root.GetProperty("audioStreams").EnumerateArray())
                {
                    int    br  = s.TryGetProperty("bitrate", out var brEl) ? brEl.GetInt32()  : 0;
                    string? su = s.TryGetProperty("url",     out var suEl) ? suEl.GetString() : null;
                    if (su != null && br > bestBitrate) { bestBitrate = br; bestUrl = su; }
                }

                if (!string.IsNullOrEmpty(bestUrl) && _player != null)
                {
                    // Reproducir directamente (equivalente a PlayStream pero sin limpiar _nativeQueue)
                    _nextPrepared = false;
                    _trackEndedReported = false;
                    _isFadingIn = false;
                    _player.Volume = 1f;
                    _player.ClearMediaItems();
                    _player.SetMediaItem(CreateMediaItem(bestUrl, title, uploader, thumb, nextVideoId));
                    _player.Prepare();
                    _player.Play();

                    _currentArtist = uploader;
                    _playedIds.Add(nextVideoId);
                    System.Diagnostics.Debug.WriteLine($"[Autoplay Native] Continuando con: {title}");

                    // Preparar cola con related del nuevo track, filtrando ya reproducidos
                    _nativeQueue.Clear();
                    if (root.TryGetProperty("relatedStreams", out var nextRelated))
                    {
                        foreach (var rel in nextRelated.EnumerateArray())
                        {
                            var vId = ExtractVideoId(rel);
                            if (!string.IsNullOrEmpty(vId) && vId != nextVideoId && !_playedIds.Contains(vId))
                                _nativeQueue.Enqueue(vId);
                        }
                    }

                    // Fallback: buscar por artista
                    if (_nativeQueue.Count == 0 && !string.IsNullOrEmpty(uploader))
                        await EnqueueFromArtistSearchAsync(uploader);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Autoplay Native] Error: {ex.Message}");
            }
            finally { _isFetchingNext = false; }
        }

        private MediaItem CreateMediaItem(string url, string title, string artist, string thumbUrl, string videoId)
        {
            var metadata = new MediaMetadata.Builder()
                .SetTitle(title)
                .SetArtist(artist)
                .SetArtworkUri(global::Android.Net.Uri.Parse(thumbUrl))
                .Build();

            return new MediaItem.Builder()
                .SetUri(url)
                .SetMediaId(videoId)
                .SetCustomCacheKey(videoId)
                .SetMediaMetadata(metadata)
                .Build();
        }

        public void Pause()  { _player?.Pause(); PostMediaNotification(); }
        public void Resume() { _player?.Play(); PostMediaNotification(); }
        public void SeekTo(long positionMs) => _player?.SeekTo(positionMs);

        public long CurrentPosition => _player?.CurrentPosition ?? 0;
        public long Duration        => _player?.Duration        ?? 0;
        public bool IsPlaying       => _player?.IsPlaying       ?? false;

        private void CreateNotificationChannel()
        {
            if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.O) return;

            var nm = (global::Android.App.NotificationManager)GetSystemService(NotificationService)!;

            // Borrar canal viejo para forzar que los cambios de importancia se apliquen
            // (Android no actualiza canales existentes)
            nm.DeleteNotificationChannel(CHANNEL_ID);

            var channel = new global::Android.App.NotificationChannel(
                CHANNEL_ID,
                "eMusicApp - Reproducción",
                global::Android.App.NotificationImportance.Default)
            {
                Description = "Controles de reproducción de música"
            };
            channel.SetShowBadge(false);
            channel.SetSound(null, null);
            channel.EnableVibration(false);
            channel.LockscreenVisibility = global::Android.App.NotificationVisibility.Public;
            nm.CreateNotificationChannel(channel);
        }

        public override void OnDestroy()
        {
            _progressHandler?.RemoveCallbacks(_progressRunnable);
            _progressHandler  = null;
            _progressRunnable = null;

            NativeAudioController.OnPlayRequested        = null;
            NativeAudioController.OnPauseRequested       = null;
            NativeAudioController.OnResumeRequested      = null;
            NativeAudioController.OnSeekRequested        = null;
            NativeAudioController.OnUpdateQueueRequested = null;

            _mediaSession?.Player?.Release();
            _mediaSession?.Release();
            _simpleCache?.Release();
            _simpleCache      = null;
            _databaseProvider = null;
            _mediaSession     = null;
            Instance          = null;
            base.OnDestroy();
        }
    }
}
