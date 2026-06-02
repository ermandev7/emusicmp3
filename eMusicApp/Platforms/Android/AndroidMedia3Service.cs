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
    [IntentFilter(new[] { "androidx.media3.session.MediaSessionService" })]
    public class AndroidMedia3Service : MediaSessionService
    {
        private MediaSession? _mediaSession;
        private IExoPlayer? _player;

        public static AndroidMedia3Service? Instance { get; private set; }

        private System.Timers.Timer? _progressTimer;

        // Cola nativa para autoplay infinito
        private Queue<string> _nativeQueue = new Queue<string>();
        private bool _isFetchingNext = false;
        private bool _nextPrepared = false;
        private string? _currentMediaId;
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = System.TimeSpan.FromSeconds(30) };

        // Caché LRU 500 MB
        private SimpleCache? _simpleCache;
        private StandaloneDatabaseProvider? _databaseProvider;

        public override void OnCreate()
        {
            base.OnCreate();
            Instance = this;

            // Puente MAUI → Nativo
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
                // Iniciar pre-fetch del siguiente inmediatamente al actualizar la cola
                if (_nativeQueue.Count > 0 && !_isFetchingNext)
                    _ = FetchNextTrackNativelyAsync();
            };

            // Caché LRU 500 MB
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

            // ExoPlayer con WakeLock de red — sigue reproduciendo con pantalla apagada
            _player = new ExoPlayerBuilder(this)
                .SetMediaSourceFactory(mediaSourceFactory)
                .SetWakeMode(C.WakeModeNetwork)
                .SetHandleAudioBecomingNoisy(true)
                .Build();

            // MediaSession con Callback que habilita todos los controles nativos
            _mediaSession = new MediaSession.Builder(this, _player)
                .SetCallback(new AllCommandsCallback())
                .Build();

            // Ícono de notificación estándar (evita crash silencioso de notificación)
            var notifProvider = new DefaultMediaNotificationProvider.Builder(this).Build();
            notifProvider.SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay);
            SetMediaNotificationProvider(notifProvider);

            // Timer de progreso: solo actualiza la UI de MAUI (no es crítico con pantalla apagada)
            _progressTimer = new System.Timers.Timer(1000);
            _progressTimer.Elapsed += OnProgressTick;
            _progressTimer.Start();
        }

        // ── Timer tick — TODA la lectura de ExoPlayer debe hacerse en el Main Thread ──
        private void OnProgressTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_player == null) return;

                var state       = _player.PlaybackState;   // Seguro en Main Thread
                bool isBuffering = (state == 2);           // STATE_BUFFERING
                bool isPlaying   = _player.IsPlaying;

                NativeAudioController.ReportBufferingState(isBuffering);
                if (!isBuffering)
                    NativeAudioController.ReportPlaybackState(isPlaying);

                if (isPlaying)
                {
                    long dur  = _player.Duration;
                    long pos  = _player.CurrentPosition;
                    int durMs = dur < 0 ? 0 : (int)dur;
                    int posMs = pos < 0 ? 0 : (int)pos;

                    NativeAudioController.ReportProgress(posMs, durMs);

                    // Detectar cambio de track (ExoPlayer ya transitó al siguiente)
                    var playingId = _player.CurrentMediaItem?.MediaId;
                    if (!string.IsNullOrEmpty(playingId) && playingId != _currentMediaId)
                    {
                        _currentMediaId = playingId;
                        var title  = _player.CurrentMediaItem?.MediaMetadata?.Title?.ToString()      ?? "";
                        var artist = _player.CurrentMediaItem?.MediaMetadata?.Artist?.ToString()     ?? "";
                        var thumb  = _player.CurrentMediaItem?.MediaMetadata?.ArtworkUri?.ToString() ?? "";
                        NativeAudioController.ReportTrackStarted(playingId, title, artist, thumb, durMs);

                        // Track cambió → pre-fetch del siguiente
                        _nextPrepared = false;
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
            });
        }

        public override MediaSession? OnGetSession(MediaSession.ControllerInfo controllerInfo)
            => _mediaSession;

        // ── Reproducción ──
        public void PlayStream(string url, string title, string artist, string thumbUrl, string videoId)
        {
            if (_player == null) return;
            _nextPrepared = false;
            _player.ClearMediaItems();
            _player.SetMediaItem(CreateMediaItem(url, title, artist, thumbUrl, videoId));
            _player.Prepare();
            _player.Play();
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
                .SetCustomCacheKey(videoId)   // Caché ignora tokens temporales de la URL
                .SetMediaMetadata(metadata)
                .Build();
        }

        // ── Pre-fetch de la siguiente pista y añadir a la cola de ExoPlayer ──
        // ExoPlayer transiciona automáticamente — funciona con pantalla apagada
        public async Task FetchNextTrackNativelyAsync()
        {
            if (_isFetchingNext || _nextPrepared || _nativeQueue.Count == 0) return;
            _isFetchingNext = true;
            try
            {
                var nextVideoId = _nativeQueue.Dequeue();
                System.Diagnostics.Debug.WriteLine($"[Autoplay] Pre-fetching: {nextVideoId}");

                var response = await _httpClient.GetStringAsync(
                    $"http://emusicmp3.duckdns.org:5050/api/streams/{nextVideoId}");
                using var doc  = JsonDocument.Parse(response);
                var root       = doc.RootElement;

                var title    = root.GetProperty("title").GetString()        ?? "Unknown";
                var uploader = root.GetProperty("uploader").GetString()     ?? "";
                var thumb    = root.GetProperty("thumbnailUrl").GetString() ?? "";

                // Seleccionar el stream de audio de mayor bitrate
                string? bestUrl    = null;
                int     bestBitrate = 0;
                foreach (var s in root.GetProperty("audioStreams").EnumerateArray())
                {
                    int    br  = s.TryGetProperty("bitrate",  out var brEl)  ? brEl.GetInt32()    : 0;
                    string? su = s.TryGetProperty("url",      out var suEl)  ? suEl.GetString()   : null;
                    if (su != null && br > bestBitrate) { bestBitrate = br; bestUrl = su; }
                }

                if (!string.IsNullOrEmpty(bestUrl) && _player != null)
                {
                    // AddMediaItem → ExoPlayer hace la transición gapless automáticamente
                    _player.AddMediaItem(CreateMediaItem(bestUrl, title, uploader, thumb, nextVideoId));
                    _nextPrepared = true;
                    System.Diagnostics.Debug.WriteLine($"[Autoplay] Siguiente en cola: {title}");

                    // Regenerar cola con relatedStreams para autoplay infinito
                    if (root.TryGetProperty("relatedStreams", out var related))
                    {
                        _nativeQueue.Clear();
                        foreach (var rel in related.EnumerateArray())
                        {
                            if (rel.TryGetProperty("videoId", out var vIdEl))
                            {
                                var vId = vIdEl.GetString();
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
            finally
            {
                _isFetchingNext = false;
            }
        }

        public void Pause()   => _player?.Pause();
        public void Resume()  => _player?.Play();
        public void SeekTo(long positionMs) => _player?.SeekTo(positionMs);

        public long CurrentPosition => _player?.CurrentPosition ?? 0;
        public long Duration        => _player?.Duration        ?? 0;
        public bool IsPlaying       => _player?.IsPlaying       ?? false;

        public override void OnDestroy()
        {
            _progressTimer?.Stop();
            _progressTimer?.Dispose();
            _progressTimer = null;

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

        // ── MediaSessionCallback: habilita Play, Pause, Siguiente, Anterior ──
        // en la notificación, pantalla de bloqueo y Android Auto
        private class AllCommandsCallback : Java.Lang.Object, MediaSession.ICallback
        {
            public MediaSession.ConnectionResult OnConnect(
                MediaSession session, MediaSession.ControllerInfo controller)
            {
                return MediaSession.ConnectionResult.Accept(
                    MediaSession.ConnectionResult.DefaultSessionCommands,
                    MediaSession.ConnectionResult.DefaultPlayerCommands);
            }
        }
    }
}
