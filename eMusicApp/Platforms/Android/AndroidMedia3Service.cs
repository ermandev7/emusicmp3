using Android.App;
using Android.Content;
using AndroidX.Core.App;
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
    [IntentFilter(new[] { "androidx.media3.session.MediaSessionService" })]
    public class AndroidMedia3Service : MediaSessionService
    {
        private MediaSession? _mediaSession;
        private IExoPlayer? _player;

        public static AndroidMedia3Service? Instance { get; private set; }

        private global::Android.App.NotificationManager? _notificationManager;
        private global::Android.OS.Handler?  _progressHandler;
        private global::Java.Lang.Runnable?  _progressRunnable;

        // Cola nativa para autoplay infinito
        private Queue<string> _nativeQueue = new Queue<string>();
        private bool _isFetchingNext = false;
        private bool _nextPrepared = false;
        private string? _currentMediaId;
        private bool _trackEndedReported = false;
        private bool _lastNotifIsPlaying = false;

        // Artwork cache para la notificación
        private global::Android.Graphics.Bitmap? _artworkBitmap;
        private string? _artworkBitmapUrl;
        private bool _isLoadingArtwork;

        private static readonly HttpClient _httpClient = new HttpClient
        {
            Timeout = System.TimeSpan.FromSeconds(30)
        };

        // Caché LRU 500 MB
        private SimpleCache? _simpleCache;
        private StandaloneDatabaseProvider? _databaseProvider;

        // Notificación
        private const string CHANNEL_ID      = "emusic_playback";
        private const int    NOTIFICATION_ID  = 1001;
        private const string ACTION_PREV      = "emusic.ACTION_PREV";
        private const string ACTION_PLAY_PAUSE = "emusic.ACTION_PLAY_PAUSE";
        private const string ACTION_NEXT      = "emusic.ACTION_NEXT";

        public override void OnCreate()
        {
            base.OnCreate();
            Instance = this;

            _notificationManager = (global::Android.App.NotificationManager)GetSystemService(NotificationService)!;
            CreateNotificationChannel();

            // ── Puente MAUI → Nativo ──
            NativeAudioController.OnPlayRequested = (url, title, artist, thumb, videoId) =>
                PlayStream(url, title, artist, thumb, videoId);
            NativeAudioController.OnPauseRequested  = Pause;
            NativeAudioController.OnResumeRequested = Resume;
            NativeAudioController.OnSeekRequested   = (pos) => SeekTo(pos);
            NativeAudioController.OnUpdateQueueRequested = (videoIds) =>
            {
                _nativeQueue.Clear();
                foreach (var id in videoIds) _nativeQueue.Enqueue(id);
                _nextPrepared = false;
            };

            // ── Caché LRU 500 MB ──
            var cacheDir = new Java.IO.File(CacheDir, "emusic_audio_cache");
            _databaseProvider = new StandaloneDatabaseProvider(this);
            var evictor = new LeastRecentlyUsedCacheEvictor(500 * 1024 * 1024);
            _simpleCache = new SimpleCache(cacheDir, evictor, _databaseProvider);

            var httpDataSourceFactory = new DefaultHttpDataSource.Factory()
                .SetAllowCrossProtocolRedirects(true);
            var cacheDataSourceFactory = new CacheDataSource.Factory()
                .SetCache(_simpleCache)
                .SetUpstreamDataSourceFactory(httpDataSourceFactory)
                .SetFlags(CacheDataSource.FlagIgnoreCacheOnError);
            var mediaSourceFactory = new DefaultMediaSourceFactory(this)
                .SetDataSourceFactory(cacheDataSourceFactory);

            // ── AudioAttributes para música + foco de audio correcto ──
            var audioAttributes = new AndroidX.Media3.Common.AudioAttributes.Builder()
                .SetUsage(C.UsageMedia)
                .SetContentType(C.AudioContentTypeMusic)
                .Build();

            // ── ExoPlayer ──
            _player = new ExoPlayerBuilder(this)
                .SetMediaSourceFactory(mediaSourceFactory)
                .SetWakeMode(C.WakeModeNetwork)
                .SetHandleAudioBecomingNoisy(true)
                .SetAudioAttributes(audioAttributes, /* handleAudioFocus= */ true)
                .Build();

            // ── MediaSession — necesario para MediaStyle y botones de hardware/Bluetooth ──
            _mediaSession = new MediaSession.Builder(this, _player)
                .Build();

            // ── Handler de progreso en MainLooper (más fiable que System.Timers.Timer en servicios) ──
            _progressHandler  = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
            _progressRunnable = new global::Java.Lang.Runnable(OnProgressTick);
            _progressHandler.PostDelayed(_progressRunnable, 500);
        }

        // ── Media3 delega la notificación a nuestra implementación manual ──
        public override void OnUpdateNotification(MediaSession session, bool startInForeground)
        {
            // Ignoramos DefaultMediaNotificationProvider; gestionamos la notificación nosotros
            if (_player?.CurrentMediaItem != null || startInForeground)
                BuildAndShowNotification(startInForeground);
        }

        // ── Botones de la notificación reciben intents aquí ──
        public override StartCommandResult OnStartCommand(global::Android.Content.Intent? intent, StartCommandFlags flags, int startId)
        {
            switch (intent?.Action)
            {
                case ACTION_PREV:
                    if (_player != null)
                    {
                        if (_player.CurrentPosition > 3000)
                            _player.SeekTo(0);
                        else
                            _player.SeekToPreviousMediaItem();
                    }
                    break;
                case ACTION_PLAY_PAUSE:
                    if (_player?.IsPlaying == true) Pause(); else Resume();
                    break;
                case ACTION_NEXT:
                    if (_player != null)
                    {
                        if (_player.HasNextMediaItem)
                            _player.SeekToNextMediaItem();
                        else if (_nativeQueue.Count > 0)
                            _ = FetchNextTrackNativelyAsync();
                    }
                    break;
            }
            return base.OnStartCommand(intent, flags, startId);
        }

        // ── Construye y muestra la notificación MediaStyle manualmente ──
        private void BuildAndShowNotification(bool startInForeground = false)
        {
            if (_player == null) return;

            var metadata  = _player.MediaMetadata;
            string title  = metadata?.Title?.ToString()  ?? "eMusicApp";
            string artist = metadata?.Artist?.ToString() ?? "";
            bool isPlaying = _player.IsPlaying;

            // Intent para abrir la app al tocar la notificación
            var openIntent = PackageManager!.GetLaunchIntentForPackage(PackageName!)!;
            var contentPi  = global::Android.App.PendingIntent.GetActivity(
                this, 0, openIntent,
                global::Android.App.PendingIntentFlags.Immutable |
                global::Android.App.PendingIntentFlags.UpdateCurrent);

            // PendingIntents para los botones
            var prevPi   = MakeServicePi(ACTION_PREV, 10);
            var togglePi = MakeServicePi(ACTION_PLAY_PAUSE, 11);
            var nextPi   = MakeServicePi(ACTION_NEXT, 12);

            // MediaStyle — enlazar con la MediaSession es OBLIGATORIO para que
            // Samsung One UI muestre el widget en la pantalla apagada/bloqueada
            var mediaStyle = new global::AndroidX.Media.App.NotificationCompat.MediaStyle()
                .SetMediaSession(_mediaSession?.SessionCompatToken)
                .SetShowActionsInCompactView(0, 1, 2); // prev · play/pause · next

            int  playPauseIcon  = isPlaying ? Resource.Drawable.pause : Resource.Drawable.play;
            string playPauseLabel = isPlaying ? "Pausar" : "Reproducir";

            var builder = new NotificationCompat.Builder(this, CHANNEL_ID)
                .SetSmallIcon(Resource.Drawable.play)
                .SetContentTitle(title)
                .SetContentText(artist)
                .SetContentIntent(contentPi)
                .SetStyle(mediaStyle)
                .SetVisibility(NotificationCompat.VisibilityPublic)
                .SetOngoing(isPlaying)
                .SetSilent(true)
                .SetShowWhen(false)
                .AddAction(Resource.Drawable.previous, "Anterior", prevPi)
                .AddAction(playPauseIcon, playPauseLabel, togglePi)
                .AddAction(Resource.Drawable.next, "Siguiente", nextPi);

            // Artwork cacheado — solo lanzar descarga si no hay otra en curso
            if (_artworkBitmap != null && _artworkBitmapUrl == metadata?.ArtworkUri?.ToString())
                builder.SetLargeIcon(_artworkBitmap);
            else if (!string.IsNullOrEmpty(metadata?.ArtworkUri?.ToString()) && !_isLoadingArtwork)
                _ = LoadArtworkAndUpdateAsync(metadata!.ArtworkUri!.ToString()!);

            var notification = builder.Build();

            // Siempre StartForeground mientras hay media cargada
            StartForeground(NOTIFICATION_ID, notification);
        }

        private global::Android.App.PendingIntent MakeServicePi(string action, int requestCode)
        {
            var intent = new global::Android.Content.Intent(this, typeof(AndroidMedia3Service))
                .SetAction(action);
            return global::Android.App.PendingIntent.GetService(
                this, requestCode, intent,
                global::Android.App.PendingIntentFlags.Immutable |
                global::Android.App.PendingIntentFlags.UpdateCurrent)!;
        }

        private async Task LoadArtworkAndUpdateAsync(string url)
        {
            _isLoadingArtwork = true;
            try
            {
                var bytes  = await _httpClient.GetByteArrayAsync(url);
                var bitmap = global::Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                if (bitmap == null) return;
                _artworkBitmap    = bitmap;
                _artworkBitmapUrl = url;
                // Re-publicar notificación con la portada ya cargada
                BuildAndShowNotification(false);
            }
            catch { /* artwork opcional */ }
            finally
            {
                _isLoadingArtwork = false;
            }
        }

        private void CreateNotificationChannel()
        {
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            {
                if (_notificationManager!.GetNotificationChannel(CHANNEL_ID) == null)
                {
                    var channel = new global::Android.App.NotificationChannel(
                        CHANNEL_ID,
                        "eMusicApp - Reproducción",
                        global::Android.App.NotificationImportance.Default)
                    {
                        Description = "Controles de reproducción de música"
                    };
                    channel.SetShowBadge(false);
                    channel.SetSound(null, null);           // Sin sonido al actualizar
                    channel.EnableVibration(false);          // Sin vibración
                    channel.LockscreenVisibility = global::Android.App.NotificationVisibility.Public;
                    _notificationManager.CreateNotificationChannel(channel);
                }
            }
        }

        public override MediaSession? OnGetSession(MediaSession.ControllerInfo controllerInfo)
            => _mediaSession;

        // ── Tick de progreso — corre en MainLooper (Handler), acceso directo a ExoPlayer ──
        private void OnProgressTick()
        {
            // Programar el siguiente tick antes de hacer trabajo (evita skip si el trabajo tarda)
            _progressHandler?.PostDelayed(_progressRunnable!, 500);

            if (_player == null) return;

            var state        = _player.PlaybackState;
            bool isBuffering = (state == 2); // STATE_BUFFERING
            bool isPlaying   = _player.IsPlaying;
            bool isEnded     = (state == 4); // STATE_ENDED

            NativeAudioController.ReportBufferingState(isBuffering);
            if (!isBuffering)
            {
                NativeAudioController.ReportPlaybackState(isPlaying);
                // Actualizar ícono play/pause en notificación cuando cambia el estado
                if (isPlaying != _lastNotifIsPlaying && _player?.CurrentMediaItem != null)
                {
                    _lastNotifIsPlaying = isPlaying;
                    BuildAndShowNotification(false);
                }
            }

            // Detectar fin natural de reproducción
            if (isEnded && !_trackEndedReported)
            {
                _trackEndedReported = true;
                NativeAudioController.ReportTrackEnded();
            }
            else if (!isEnded)
            {
                _trackEndedReported = false;
            }

            // Progreso y detección de cambio de track (siempre, no solo cuando isPlaying=true)
            if (_player.CurrentMediaItem != null)
            {
                long dur  = _player.Duration;
                long pos  = _player.CurrentPosition;
                int durMs = dur < 0 ? 0 : (int)dur;
                int posMs = pos < 0 ? 0 : (int)pos;

                NativeAudioController.ReportProgress(posMs, durMs);

                // Auto-avance nativo de ExoPlayer: detectar cambio de MediaId
                var playingId = _player.CurrentMediaItem?.MediaId;
                if (!string.IsNullOrEmpty(playingId) && playingId != _currentMediaId)
                {
                    _currentMediaId = playingId;
                    var title  = _player.CurrentMediaItem?.MediaMetadata?.Title?.ToString()      ?? "";
                    var artist = _player.CurrentMediaItem?.MediaMetadata?.Artist?.ToString()     ?? "";
                    var thumb  = _player.CurrentMediaItem?.MediaMetadata?.ArtworkUri?.ToString() ?? "";
                    NativeAudioController.ReportTrackStarted(playingId, title, artist, thumb, durMs);

                    _nextPrepared = false;
                    _trackEndedReported = false;
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

        // ── Reproducir ──
        public void PlayStream(string url, string title, string artist, string thumbUrl, string videoId)
        {
            if (_player == null) return;

            _nextPrepared = false;
            _trackEndedReported = false;
            _nativeQueue.Clear();
            _artworkBitmap    = null;
            _artworkBitmapUrl = null;
            _isLoadingArtwork = false;

            _player.ClearMediaItems();
            _player.SetMediaItem(CreateMediaItem(url, title, artist, thumbUrl, videoId));
            _player.Prepare();
            _player.Play();

            BuildAndShowNotification(true);

            // Pre-cargar siguiente track para autoplay sin interrupciones
            _ = FetchRelatedAndQueueNextAsync(videoId);
        }

        private async Task FetchRelatedAndQueueNextAsync(string videoId)
        {
            try
            {
                var response = await _httpClient.GetStringAsync(
                    $"{AppConstants.ApiBaseUrl}/streams/{videoId}");
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                _nativeQueue.Clear();
                if (root.TryGetProperty("relatedStreams", out var related))
                {
                    foreach (var rel in related.EnumerateArray())
                    {
                        var vId = rel.TryGetProperty("videoId", out var vIdEl) ? vIdEl.GetString() : null;
                        if (!string.IsNullOrEmpty(vId)) _nativeQueue.Enqueue(vId);
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[Autoplay] Cola: {_nativeQueue.Count} canciones");

                if (_nativeQueue.Count > 0 && !_isFetchingNext)
                    await FetchNextTrackNativelyAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Autoplay] Error related: {ex.Message}");
            }
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

                    // Actualizar cola con los relacionados del siguiente
                    if (root.TryGetProperty("relatedStreams", out var related))
                    {
                        _nativeQueue.Clear();
                        foreach (var rel in related.EnumerateArray())
                        {
                            var vId = rel.TryGetProperty("videoId", out var vIdEl) ? vIdEl.GetString() : null;
                            if (!string.IsNullOrEmpty(vId)) _nativeQueue.Enqueue(vId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Autoplay] Error: {ex.Message}");
                _nextPrepared = false;
            }
            finally
            {
                _isFetchingNext = false;
            }
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

        public void Pause()
        {
            _player?.Pause();
            BuildAndShowNotification(false);
        }

        public void Resume()
        {
            _player?.Play();
            BuildAndShowNotification(false);
        }
        public void SeekTo(long positionMs) => _player?.SeekTo(positionMs);

        public long CurrentPosition => _player?.CurrentPosition ?? 0;
        public long Duration        => _player?.Duration        ?? 0;
        public bool IsPlaying       => _player?.IsPlaying       ?? false;

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
