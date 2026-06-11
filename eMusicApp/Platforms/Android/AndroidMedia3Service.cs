using Android.App;
using Android.Content;
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
    [IntentFilter(new[] { "androidx.media3.session.MediaLibraryService", "android.media.browse.MediaBrowserService" },
        Categories = new[] { "android.intent.category.DEFAULT" })]
    public class AndroidMedia3Service : MediaLibraryService
    {
        private MediaLibraryService.MediaLibrarySession? _mediaSession;
        private IExoPlayer? _player;
        private SkipAwareForwardingPlayer? _wrappedPlayer;

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

        // IDs y títulos ya reproducidos para evitar loops y duplicados
        private readonly HashSet<string> _playedIds = new HashSet<string>();
        private readonly HashSet<string> _playedTitles = new HashSet<string>();

        private static string NormalizeTitle(string title)
        {
            if (string.IsNullOrEmpty(title)) return "";
            var t = title.ToLowerInvariant();
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\([^)]*\)", "");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"\[[^\]]*\]", "");
            var dashIdx = t.IndexOf(" - ");
            if (dashIdx > 3) t = t.Substring(0, dashIdx);
            foreach (var kw in new[] {
                "official", "video", "audio", "lyric", "lyrics", "live",
                "cover", "karaoke", "remix", "acoustic", "version", "hd", "4k", "ft.", "feat.",
                "remaster", "remastered", "explicit",
                // Keywords en español
                "letra", "letras", "en vivo", "vivo", "directo", "en directo",
                "completa", "completo", "concierto", "acustico", "acústico",
                "tema original", "sencillo", "estreno" })
                t = t.Replace(kw, "");
            t = t.Replace("á", "a").Replace("é", "e").Replace("í", "i").Replace("ó", "o").Replace("ú", "u").Replace("ñ", "n");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"[^a-z0-9]", "");
            return t;
        }

        /// <summary>
        /// Verifica si un título ya fue reproducido usando contains-match:
        /// "nuncamefaltes" matchea contra "nuncamefaltesenvivo" y viceversa.
        /// </summary>
        private bool IsTitleAlreadyPlayed(string norm)
        {
            if (string.IsNullOrEmpty(norm)) return false;
            foreach (var played in _playedTitles)
            {
                if (norm.Contains(played) || played.Contains(norm))
                    return true;
            }
            return false;
        }

        private bool IsGoodNativeTrack(string? videoId, string? title)
        {
            if (string.IsNullOrEmpty(videoId)) return false;
            if (_playedIds.Contains(videoId)) return false;
            if (!string.IsNullOrEmpty(title))
            {
                var norm = NormalizeTitle(title);
                if (!string.IsNullOrEmpty(norm) && IsTitleAlreadyPlayed(norm)) return false;
                var lower = title.ToLowerInvariant();
                if (lower.Contains("karaoke") || lower.Contains("instrumental")
                    || lower.Contains("cover") || lower.Contains("tribute")
                    || lower.Contains("pista") || lower.Contains("backing"))
                    return false;
            }
            return true;
        }

        private void MarkNativeAsPlayed(string videoId, string title)
        {
            _playedIds.Add(videoId);
            var norm = NormalizeTitle(title);
            if (!string.IsNullOrEmpty(norm)) _playedTitles.Add(norm);
        }

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

        // Acciones para botones de la notificación (via BroadcastReceiver, no PendingIntent a servicio)
        public const string ACTION_PREV       = "emusic.ACTION_PREV";
        public const string ACTION_PLAY_PAUSE = "emusic.ACTION_PLAY_PAUSE";
        public const string ACTION_NEXT       = "emusic.ACTION_NEXT";

        // Artwork cacheado para la notificación
        private global::Android.Graphics.Bitmap? _artworkBitmap;
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

            // ── Buffer agresivo para conducción (túneles / zonas sin cobertura) ──
            // MinBuffer 60s: ExoPlayer intentará mantener al menos 60s de audio en RAM.
            // MaxBuffer 120s: tope de lo que almacena antes de pausar la descarga.
            // BufferForPlayback 2.5s: mínimo para arrancar reproducción (rápido).
            // BufferForPlaybackAfterRebuffer 5s: mínimo tras un rebuffer (un poco más conservador).
            var loadControl = new DefaultLoadControl.Builder()
                .SetBufferDurationsMs(
                    /* minBufferMs */                60_000,
                    /* maxBufferMs */               120_000,
                    /* bufferForPlaybackMs */          2_500,
                    /* bufferForPlaybackAfterRebufferMs */ 5_000)
                .SetPrioritizeTimeOverSizeThresholds(true)
                .Build();

            var audioAttributes = new AndroidX.Media3.Common.AudioAttributes.Builder()
                .SetUsage(C.UsageMedia)
                .SetContentType(C.AudioContentTypeMusic)
                .Build();

            _player = new ExoPlayerBuilder(this)
                .SetMediaSourceFactory(new DefaultMediaSourceFactory(this).SetDataSourceFactory(cacheDataSourceFactory))
                .SetLoadControl(loadControl)
                .SetWakeMode(C.WakeModeNetwork)
                .SetHandleAudioBecomingNoisy(true)
                .SetAudioAttributes(audioAttributes, true)
                .Build();

            _wrappedPlayer = new SkipAwareForwardingPlayer(_player, this);

            // SessionActivity: PendingIntent para abrir la app desde Android Auto / notificación.
            // Sin esto, Android Auto muestra "Desbloquea el teléfono".
            var sessionActivityIntent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "");
            PendingIntent? sessionActivity = null;
            if (sessionActivityIntent != null)
                sessionActivity = PendingIntent.GetActivity(this, 0, sessionActivityIntent,
                    PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

            var sessionBuilder = new MediaLibraryService.MediaLibrarySession.Builder(this, _wrappedPlayer, new LibraryCallback());
            if (sessionActivity != null)
                sessionBuilder.SetSessionActivity(sessionActivity);
            _mediaSession = sessionBuilder.Build();

            // Configurar el provider de notificaciones de Media3 para que genere
            // botones funcionales (Play/Pause/Next/Prev) enrutados por MediaSession
            SetMediaNotificationProvider(new DefaultMediaNotificationProvider(this));

            _progressHandler  = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
            _progressRunnable = new global::Java.Lang.Runnable(OnProgressTick);
            _progressHandler.PostDelayed(_progressRunnable, 500);

            // Promover a foreground inmediatamente (Android exige StartForeground en <5s)
            try
            {
                base.OnUpdateNotification(_mediaSession, true);
            }
            catch
            {
                PostMediaNotification();
            }

            // Flush pending play: si MAUI pidió reproducir mientras el servicio estaba muerto
            if (NativeAudioController.PendingPlayRequest is var pending && pending != null)
            {
                var p = pending.Value;
                NativeAudioController.PendingPlayRequest = null;
                System.Diagnostics.Debug.WriteLine($"[Service] Flushing pending play: {p.title}");
                PlayStream(p.url, p.title, p.artist, p.thumb, p.videoId);
            }
        }

        public override MediaLibraryService.MediaLibrarySession? OnGetSessionFromMediaLibraryService(MediaSession.ControllerInfo? controllerInfo)
            => _mediaSession;

        public override MediaSession? OnGetSession(MediaSession.ControllerInfo? controllerInfo)
            => _mediaSession;

        public override void OnUpdateNotification(MediaSession session, bool startInForegroundRequired)
        {
            // Intentar primero que Media3 genere su propia notificación con MediaSession transport controls
            try
            {
                base.OnUpdateNotification(session, startInForegroundRequired);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Notification] base.OnUpdateNotification failed: {ex.Message}");
                // Fallback: notificación custom si Media3 falla
                PostMediaNotification();
            }
        }

        public override StartCommandResult OnStartCommand(Intent? intent, global::Android.App.StartCommandFlags flags, int startId)
        {
            return base.OnStartCommand(intent, flags, startId);
        }

        /// <summary>
        /// Crea un PendingIntent de Broadcast para los botones de la notificación.
        /// Los broadcasts NO se throttlean (a diferencia de PendingIntent.GetForegroundService).
        /// El MediaButtonReceiver interno los despacha al instante.
        /// </summary>
        private PendingIntent BuildBroadcastPendingIntent(string action)
        {
            var intent = new Intent(action);
            intent.SetComponent(new ComponentName(this, Java.Lang.Class.FromType(typeof(MediaButtonReceiver))));
            return PendingIntent.GetBroadcast(this, action.GetHashCode(), intent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)!;
        }

        private bool _isPostingNotification;
        private void PostMediaNotification()
        {
            if (_isPostingNotification) return;
            _isPostingNotification = true;
            try
            {
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
                        BuildBroadcastPendingIntent(ACTION_PREV)).Build())
                    .AddAction(new Notification.Action.Builder(
                        isPlaying ? global::Android.Resource.Drawable.IcMediaPause
                                  : global::Android.Resource.Drawable.IcMediaPlay,
                        isPlaying ? "Pausa" : "Play",
                        BuildBroadcastPendingIntent(ACTION_PLAY_PAUSE)).Build())
                    .AddAction(new Notification.Action.Builder(
                        global::Android.Resource.Drawable.IcMediaNext, "Siguiente",
                        BuildBroadcastPendingIntent(ACTION_NEXT)).Build());

                var style = new Notification.MediaStyle()
                    .SetShowActionsInCompactView(0, 1, 2);
                try
                {
                    if (_mediaSession != null)
                    {
                        var token = _mediaSession.PlatformToken;
                        if (token is global::Android.Media.Session.MediaSession.Token platformToken)
                        {
                            style.SetMediaSession(platformToken);
                        }
                        else if (token != null)
                        {
                            // Fallback: el tipo .NET no matchea pero el objeto Java sí es un token válido
                            var tokenFromHandle = global::Android.Runtime.Extensions.JavaCast<global::Android.Media.Session.MediaSession.Token>(token);
                            if (tokenFromHandle != null)
                                style.SetMediaSession(tokenFromHandle);
                        }
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[Notification] Token error: {ex.Message}");
                }
                builder.SetStyle(style);

                if (_artworkBitmap != null)
                    builder.SetLargeIcon(_artworkBitmap);

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
                _artworkBitmap = global::Android.Graphics.BitmapFactory.DecodeByteArray(bytes, 0, bytes.Length);
                PostMediaNotification();
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
                    var title  = _player.CurrentMediaItem?.MediaMetadata?.Title?.ToString()      ?? "";
                    var artist = _player.CurrentMediaItem?.MediaMetadata?.Artist?.ToString()     ?? "";
                    MarkNativeAsPlayed(playingId, title);
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

                    if (!_isFetchingNext)
                    {
                        if (_nativeQueue.Count > 0)
                            _ = FetchNextTrackNativelyAsync();
                        else
                            _ = FetchRelatedAndQueueNextAsync(playingId);
                    }
                }
            }

            if (_player.PlayerError != null)
            {
                System.Diagnostics.Debug.WriteLine($"[Player] ExoPlayer error: {_player.PlayerError.Message}");
                NativeAudioController.ReportPlaybackState(false);
                _player.ClearMediaItems();
                // Intentar continuar con la siguiente canción si hay cola
                if (!_isFetchingNext && _nativeQueue.Count > 0)
                    _ = FetchNextTrackNativelyAsync();
                else if (!_isFetchingNext && !string.IsNullOrEmpty(_currentMediaId))
                    _ = AutoContinuePlaybackAsync();
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
            _currentTitle = title;
            MarkNativeAsPlayed(videoId, title);

            _player.ClearMediaItems();
            _player.SetMediaItem(CreateMediaItem(url, title, artist, thumbUrl, videoId));
            _player.Prepare();
            _player.Play();

            PostMediaNotification();
            _ = LoadArtworkAsync(thumbUrl);

            _ = FetchRelatedAndQueueNextAsync(videoId);
        }

        /// <summary>
        /// Llena la cola con búsquedas por género + artista en ratio 2:1.
        /// Search-first: no depende de relatedStreams (que trae versiones de la misma canción).
        /// </summary>
        private async Task FetchRelatedAndQueueNextAsync(string videoId)
        {
            try
            {
                _nativeQueue.Clear();
                await EnqueueSmartSearchAsync();

                // Fallback: si smart search no encontró nada, usar relatedStreams
                if (_nativeQueue.Count == 0)
                {
                    var response = await _httpClient.GetStringAsync(
                        $"{AppConstants.ApiBaseUrl}/streams/{videoId}");
                    using var doc = JsonDocument.Parse(response);
                    if (doc.RootElement.TryGetProperty("relatedStreams", out var related))
                    {
                        foreach (var rel in related.EnumerateArray())
                        {
                            var vId = ExtractVideoId(rel);
                            var title = rel.TryGetProperty("title", out var tp) ? tp.GetString() : null;
                            if (vId != videoId && IsGoodNativeTrack(vId, title) && !_nativeQueue.Contains(vId))
                                _nativeQueue.Enqueue(vId);
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[Autoplay] Cola: {_nativeQueue.Count} canciones");

                if (_nativeQueue.Count > 0 && !_isFetchingNext)
                    await FetchNextTrackNativelyAsync();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Autoplay] Error: {ex.Message}");
            }
        }

        /// <summary>
        /// Búsqueda inteligente con ratio 2:1: 2 tracks de género (otros artistas), 1 del mismo artista.
        /// Se repite hasta llenar 8+ tracks en la cola.
        /// </summary>
        private async Task EnqueueSmartSearchAsync()
        {
            var genreQueries = BuildGenreQueries(_currentTitle, _currentArtist);
            var artistQueries = BuildArtistQueries(_currentArtist);

            int gi = 0, ai = 0;

            // Rellenar con ratio 2:1 (género : artista)
            while (_nativeQueue.Count < 10 && (gi < genreQueries.Length || ai < artistQueries.Length))
            {
                // 2 del género (otros artistas)
                for (int k = 0; k < 2 && gi < genreQueries.Length; k++, gi++)
                    await SearchAndEnqueueAsync(genreQueries[gi], 2);

                // 1 del mismo artista
                if (ai < artistQueries.Length)
                    await SearchAndEnqueueAsync(artistQueries[ai++], 1);
            }

            System.Diagnostics.Debug.WriteLine($"[Autoplay] Smart search: {_nativeQueue.Count} tracks");
        }

        private async Task SearchAndEnqueueAsync(string query, int maxPerQuery)
        {
            try
            {
                var searchUrl = $"{AppConstants.ApiBaseUrl}/search?q={Uri.EscapeDataString(query)}";
                var response = await _httpClient.GetStringAsync(searchUrl);
                using var doc = JsonDocument.Parse(response);
                int added = 0;

                if (doc.RootElement.TryGetProperty("items", out var items))
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (added >= maxPerQuery) break;
                        if (item.TryGetProperty("type", out var typeEl))
                        {
                            var type = typeEl.GetString();
                            if (!string.IsNullOrEmpty(type) && type != "stream") continue;
                        }
                        var vId = ExtractVideoId(item);
                        var title = item.TryGetProperty("title", out var tp) ? tp.GetString() : null;
                        if (IsGoodNativeTrack(vId, title) && !_nativeQueue.Contains(vId!))
                        {
                            _nativeQueue.Enqueue(vId!);
                            added++;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Autoplay] Error búsqueda '{query}': {ex.Message}");
            }
        }

        private static readonly Dictionary<string, string[]> _nativeGenreMap = new()
        {
            ["salsa"] = new[] { "salsa éxitos", "salsa romántica mix", "lo mejor de la salsa", "salsa clásica", "salsa brava" },
            ["bachata"] = new[] { "bachata éxitos", "bachata romántica mix", "lo mejor de la bachata", "bachata sensual" },
            ["reggaeton"] = new[] { "reggaeton éxitos 2024", "reggaeton mix", "perreo mix", "reggaeton viejo" },
            ["cumbia"] = new[] { "cumbia éxitos", "cumbia mix bailable", "cumbia clásica" },
            ["merengue"] = new[] { "merengue éxitos", "merengue mix bailable", "merengue clásico" },
            ["vallenato"] = new[] { "vallenato éxitos", "vallenato romántico", "vallenato clásico" },
            ["rock"] = new[] { "rock en español éxitos", "rock clásico mix", "classic rock hits", "rock latino" },
            ["pop"] = new[] { "pop éxitos 2024", "pop latino mix", "pop en español", "pop hits" },
            ["rap"] = new[] { "rap éxitos", "hip hop mix", "rap en español" },
            ["trap"] = new[] { "trap latino mix", "trap éxitos 2024", "trap mix" },
            ["balada"] = new[] { "baladas románticas mix", "baladas en español", "baladas de amor" },
            ["ranchera"] = new[] { "rancheras éxitos", "música mexicana mix", "mariachi éxitos" },
            ["corrido"] = new[] { "corridos tumbados mix", "corridos éxitos", "corridos mix 2024" },
            ["reggae"] = new[] { "reggae éxitos", "reggae mix", "reggae en español" },
        };

        private static string? DetectNativeGenre(string? title, string? artist)
        {
            var combined = $"{title} {artist}".ToLowerInvariant();
            foreach (var kv in _nativeGenreMap)
                if (combined.Contains(kv.Key)) return kv.Key;
            if (combined.Contains("reggaet") || combined.Contains("perreo")) return "reggaeton";
            if (combined.Contains("bachi")) return "bachata";
            if (combined.Contains("ranchera") || combined.Contains("mariachi")) return "ranchera";
            if (combined.Contains("corrido") || combined.Contains("tumbado")) return "corrido";
            if (combined.Contains("romántic") || combined.Contains("amor")) return "balada";
            return null;
        }

        private static string[] BuildGenreQueries(string? title, string? artist)
        {
            var genre = DetectNativeGenre(title, artist);
            if (genre != null && _nativeGenreMap.TryGetValue(genre, out var gq))
                return gq.OrderBy(_ => Random.Shared.Next()).ToArray();
            // Sin género detectado — buscar genéricamente
            return new[] { "música popular mix", "éxitos latinos 2024", "lo más escuchado" };
        }

        private static string[] BuildArtistQueries(string? artist)
        {
            if (string.IsNullOrEmpty(artist)) return Array.Empty<string>();
            return new[] { $"{artist} éxitos", $"{artist} mejores canciones", $"lo mejor de {artist}" };
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
                if (idx >= 0)
                {
                    var id = url.Substring(idx + 3);
                    var ampIdx = id.IndexOf('&');
                    return ampIdx >= 0 ? id.Substring(0, ampIdx) : id;
                }
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
                    MarkNativeAsPlayed(nextVideoId, title);
                    _player.AddMediaItem(CreateMediaItem(bestUrl, title, uploader, thumb, nextVideoId));
                    _nextPrepared = true;
                    System.Diagnostics.Debug.WriteLine($"[Autoplay] Siguiente pre-cargado: {title}");

                    if (_player.PlaybackState == 4) // STATE_ENDED
                    {
                        _player.SeekToNextMediaItem();
                        _player.Prepare();
                        _player.Play();
                        System.Diagnostics.Debug.WriteLine($"[Autoplay] Re-iniciando desde ENDED: {title}");
                    }

                    // Rellenar cola si queda poco — search-first con ratio 2:1
                    if (_nativeQueue.Count < 3)
                    {
                        _currentArtist = uploader;
                        _currentTitle = title;
                        await EnqueueSmartSearchAsync();
                    }

                    // Último recurso: limpiar historial y volver a buscar
                    if (_nativeQueue.Count == 0)
                    {
                        _playedIds.Clear();
                        _playedTitles.Clear();
                        MarkNativeAsPlayed(nextVideoId, title); // no repetir la actual
                        await EnqueueSmartSearchAsync();
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
                // Rellenar la cola nativa si está vacía — search-first
                if (_nativeQueue.Count == 0)
                {
                    await EnqueueSmartSearchAsync();

                    // Último recurso: limpiar historial y reintentar
                    if (_nativeQueue.Count == 0)
                    {
                        _playedIds.Clear();
                        _playedTitles.Clear();
                        if (!string.IsNullOrEmpty(_currentMediaId))
                            MarkNativeAsPlayed(_currentMediaId, _currentTitle ?? "");
                        await EnqueueSmartSearchAsync();
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
                    _currentTitle = title;
                    MarkNativeAsPlayed(nextVideoId, title);
                    System.Diagnostics.Debug.WriteLine($"[Autoplay Native] Continuando con: {title}");

                    // Rellenar cola si queda poco — search-first
                    if (_nativeQueue.Count < 3)
                    {
                        _currentArtist = uploader;
                        _currentTitle = title;
                        await EnqueueSmartSearchAsync();
                    }
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

        // ── Handlers para BroadcastReceiver (botones de notificación) ──
        public void HandlePlayPause()
        {
            if (_player == null) return;
            if (_player.IsPlaying)
            {
                _player.Pause();
                NativeAudioController.ReportPlaybackState(false);
            }
            else
            {
                _player.Play();
                NativeAudioController.ReportPlaybackState(true);
            }
            PostMediaNotification();
        }

        public void HandleNext()
        {
            System.Diagnostics.Debug.WriteLine("[Notification] HandleNext called");
            if (_wrappedPlayer != null)
            {
                _wrappedPlayer.SeekToNext();
            }
            else if (_player != null)
            {
                NativeAudioController.OnSkipToNext?.Invoke();
                _ = FetchNextTrackNativelyAsync();
            }
            PostMediaNotification();
        }

        public void HandlePrev()
        {
            System.Diagnostics.Debug.WriteLine("[Notification] HandlePrev called");
            if (_wrappedPlayer != null)
            {
                _wrappedPlayer.SeekToPrevious();
            }
            else if (_player != null)
            {
                NativeAudioController.OnSkipToPrevious?.Invoke();
            }
            PostMediaNotification();
        }
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
            _wrappedPlayer    = null;
            Instance          = null;
            base.OnDestroy();
        }
    }

    /// <summary>
    /// Intercepta comandos de Android Auto (next/prev/play/pause/stop).
    /// - Next/Previous: si ExoPlayer no tiene items, dispara búsqueda nativa.
    /// - Siempre reporta Next/Prev como disponibles para que Android Auto muestre los botones.
    /// - Play/Pause/Stop: actualiza la notificación MediaStyle tras cada acción.
    /// </summary>
    public class SkipAwareForwardingPlayer : AndroidX.Media3.Common.ForwardingPlayer
    {
        private readonly AndroidMedia3Service _service;

        public SkipAwareForwardingPlayer(IPlayer player, AndroidMedia3Service service)
            : base(player)
        {
            _service = service;
        }

        // Constantes de Player.COMMAND_* (Media3 spec, no expuestas en .NET bindings)
        private const int CMD_STOP = 3;
        private const int CMD_SEEK_TO_PREVIOUS = 15;
        private const int CMD_SEEK_TO_PREVIOUS_MEDIA_ITEM = 16;
        private const int CMD_SEEK_TO_NEXT = 17;
        private const int CMD_SEEK_TO_NEXT_MEDIA_ITEM = 18;

        // Siempre reportar Next/Previous como disponibles para que Android Auto muestre los botones
        public override bool IsCommandAvailable(int command)
        {
            if (command == CMD_SEEK_TO_NEXT
                || command == CMD_SEEK_TO_NEXT_MEDIA_ITEM
                || command == CMD_SEEK_TO_PREVIOUS
                || command == CMD_SEEK_TO_PREVIOUS_MEDIA_ITEM
                || command == CMD_STOP)
                return true;
            return base.IsCommandAvailable(command);
        }

        public override PlayerCommands AvailableCommands
        {
            get
            {
                var cmds = base.AvailableCommands;
                return cmds.BuildUpon()
                    .Add(CMD_SEEK_TO_NEXT)
                    .Add(CMD_SEEK_TO_NEXT_MEDIA_ITEM)
                    .Add(CMD_SEEK_TO_PREVIOUS)
                    .Add(CMD_SEEK_TO_PREVIOUS_MEDIA_ITEM)
                    .Add(CMD_STOP)
                    .Build();
            }
        }

        // ── Play / Pause / Stop ──
        // Overrides explícitos para que Media3 los enrute desde la notificación y lock screen
        public override void Play()
        {
            System.Diagnostics.Debug.WriteLine("[ForwardingPlayer] Play()");
            base.Play();
            NativeAudioController.ReportPlaybackState(true);
        }

        public override void Pause()
        {
            System.Diagnostics.Debug.WriteLine("[ForwardingPlayer] Pause()");
            base.Pause();
            NativeAudioController.ReportPlaybackState(false);
        }

        public override void Stop()
        {
            System.Diagnostics.Debug.WriteLine("[ForwardingPlayer] Stop()");
            base.Stop();
            NativeAudioController.ReportPlaybackState(false);
        }

        // ── Next ──
        public override void SeekToNext()
        {
            if (HasNextMediaItem)
            {
                base.SeekToNext();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[ForwardingPlayer] SeekToNext sin items — skip nativo");
                NativeAudioController.OnSkipToNext?.Invoke();
                _ = _service.FetchNextTrackNativelyAsync();
            }
        }

        public override void SeekToNextMediaItem()
        {
            if (HasNextMediaItem)
            {
                base.SeekToNextMediaItem();
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("[ForwardingPlayer] SeekToNextMediaItem sin items — skip nativo");
                NativeAudioController.OnSkipToNext?.Invoke();
                _ = _service.FetchNextTrackNativelyAsync();
            }
        }

        // ── Previous ──
        public override void SeekToPrevious()
        {
            // Si llevamos más de 3s de reproducción, reiniciar la canción
            if (CurrentPosition > 3000)
            {
                SeekTo(0);
                return;
            }
            if (HasPreviousMediaItem)
                base.SeekToPrevious();
            else
                NativeAudioController.OnSkipToPrevious?.Invoke();
        }

        public override void SeekToPreviousMediaItem()
        {
            if (HasPreviousMediaItem)
                base.SeekToPreviousMediaItem();
            else
                NativeAudioController.OnSkipToPrevious?.Invoke();
        }

        // Media3 actualiza la notificación automáticamente al cambiar el estado del player.
    }
}
