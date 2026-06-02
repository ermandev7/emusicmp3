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

        // Cola nativa para autoplay
        private Queue<string> _nativeQueue = new Queue<string>();
        private bool _isFetchingNext = false;
        private bool _nextPrepared = false;
        private string? _currentMediaId;
        private static readonly HttpClient _httpClient = new HttpClient { Timeout = System.TimeSpan.FromSeconds(30) };

        // Caché LRU
        private SimpleCache? _simpleCache;
        private StandaloneDatabaseProvider? _databaseProvider;

        // Listener nativo de ExoPlayer — detecta cambios de canción SIN depender del hilo de MAUI
        private PlayerListener? _playerListener;

        public override void OnCreate()
        {
            base.OnCreate();
            Instance = this;

            // Puente MAUI → Nativo
            NativeAudioController.OnPlayRequested  = (url, title, artist, thumb, videoId) => PlayStream(url, title, artist, thumb, videoId);
            NativeAudioController.OnPauseRequested  = Pause;
            NativeAudioController.OnResumeRequested = Resume;
            NativeAudioController.OnSeekRequested   = (pos) => SeekTo(pos);
            NativeAudioController.OnUpdateQueueRequested = (videoIds) =>
            {
                _nativeQueue.Clear();
                foreach (var id in videoIds) _nativeQueue.Enqueue(id);
                _nextPrepared = false;
            };

            // Caché LRU 500 MB
            var cacheDir = new Java.IO.File(CacheDir, "emusic_audio_cache");
            _databaseProvider = new StandaloneDatabaseProvider(this);
            var evictor = new LeastRecentlyUsedCacheEvictor(500 * 1024 * 1024);
            _simpleCache = new SimpleCache(cacheDir, evictor, _databaseProvider);

            var httpDataSourceFactory = new DefaultHttpDataSource.Factory().SetAllowCrossProtocolRedirects(true);
            var cacheDataSourceFactory = new CacheDataSource.Factory()
                .SetCache(_simpleCache)
                .SetUpstreamDataSourceFactory(httpDataSourceFactory)
                .SetFlags(CacheDataSource.FlagIgnoreCacheOnError);

            var mediaSourceFactory = new DefaultMediaSourceFactory(this)
                .SetDataSourceFactory(cacheDataSourceFactory);

            // Construir ExoPlayer
            _player = new ExoPlayerBuilder(this)
                .SetMediaSourceFactory(mediaSourceFactory)
                .SetWakeMode(C.WakeModeNetwork)
                .SetHandleAudioBecomingNoisy(true)
                .Build();

            // ── Listener nativo: detecta MediaItem transition (canción siguiente) ──
            _playerListener = new PlayerListener(this);
            _player.AddListener(_playerListener);

            // MediaSession — esto genera automáticamente la notificación con los controles
            _mediaSession = new MediaSession.Builder(this, _player)
                .SetCallback(new MediaSessionCallback())
                .Build();

            // Proveedor de notificación con ícono del sistema
            var notifProvider = new DefaultMediaNotificationProvider.Builder(this).Build();
            notifProvider.SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay);
            SetMediaNotificationProvider(notifProvider);

            // Temporizador de progreso (solo UI de MAUI — no afecta autoplay)
            _progressTimer = new System.Timers.Timer(1000);
            _progressTimer.Elapsed += (s, e) =>
            {
                if (_player == null) return;
                var state = _player.PlaybackState;
                bool isBuffering = (state == 2);

                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                {
                    NativeAudioController.ReportBufferingState(isBuffering);
                    if (!isBuffering)
                        NativeAudioController.ReportPlaybackState(_player.IsPlaying);

                    if (_player.IsPlaying)
                    {
                        long dur = _player.Duration;
                        long pos = _player.CurrentPosition;
                        int durMs = dur < 0 ? 0 : (int)dur;
                        int posMs = pos < 0 ? 0 : (int)pos;
                        NativeAudioController.ReportProgress(posMs, durMs);

                        // Historial: detectar cambio de track
                        var playingId = _player.CurrentMediaItem?.MediaId;
                        if (!string.IsNullOrEmpty(playingId) && playingId != _currentMediaId)
                        {
                            _currentMediaId = playingId;
                            var title  = _player.CurrentMediaItem?.MediaMetadata?.Title?.ToString() ?? "";
                            var artist = _player.CurrentMediaItem?.MediaMetadata?.Artist?.ToString() ?? "";
                            var thumb  = _player.CurrentMediaItem?.MediaMetadata?.ArtworkUri?.ToString() ?? "";
                            NativeAudioController.ReportTrackStarted(playingId, title, artist, thumb, durMs);
                        }

                        // Pre-fetch siguiente cuando quedan 15 s
                        if (durMs > 0 && (durMs - posMs) <= 15000)
                        {
                            if (!_isFetchingNext && !_nextPrepared && _nativeQueue.Count > 0)
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
            };
            _progressTimer.Start();
        }

        // Android descubre la sesión aquí
        public override MediaSession? OnGetSession(MediaSession.ControllerInfo controllerInfo)
            => _mediaSession;

        // ── Reproducción ──
        public void PlayStream(string url, string title, string artist, string thumbUrl, string videoId)
        {
            if (_player == null) return;
            _nextPrepared = false;
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
                .SetCustomCacheKey(videoId)   // Caché ignora tokens de URL
                .SetMediaMetadata(metadata)
                .Build();
        }

        // ── Pre-fetch de la siguiente pista ──
        public async Task FetchNextTrackNativelyAsync()
        {
            _isFetchingNext = true;
            try
            {
                if (_nativeQueue.Count == 0) return;
                var nextVideoId = _nativeQueue.Dequeue();

                var response = await _httpClient.GetStringAsync($"http://emusicmp3.duckdns.org:5050/api/streams/{nextVideoId}");
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                var title    = root.GetProperty("title").GetString()        ?? "Unknown";
                var uploader = root.GetProperty("uploader").GetString()     ?? "Unknown";
                var thumb    = root.GetProperty("thumbnailUrl").GetString() ?? "";

                var audioStreams = root.GetProperty("audioStreams");
                if (audioStreams.GetArrayLength() > 0)
                {
                    // Seleccionar stream de mayor bitrate
                    int bestBitrate = 0;
                    string? bestUrl = null;
                    foreach (var s in audioStreams.EnumerateArray())
                    {
                        int br = s.TryGetProperty("bitrate", out var brEl) ? brEl.GetInt32() : 0;
                        string? su = s.TryGetProperty("url", out var suEl) ? suEl.GetString() : null;
                        if (su != null && br > bestBitrate) { bestBitrate = br; bestUrl = su; }
                    }
                    
                    if (!string.IsNullOrEmpty(bestUrl) && _player != null)
                    {
                        // Añadir a la cola de ExoPlayer para gapless
                        _player.AddMediaItem(CreateMediaItem(bestUrl, title, uploader, thumb, nextVideoId));
                        _nextPrepared = true;

                        // Regenerar cola con relatedStreams
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

                        System.Diagnostics.Debug.WriteLine($"[Autoplay] Siguiente preparado: {title}");
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Autoplay] Error fetch siguiente: {ex.Message}");
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

            NativeAudioController.OnPlayRequested        = null;
            NativeAudioController.OnPauseRequested       = null;
            NativeAudioController.OnResumeRequested      = null;
            NativeAudioController.OnSeekRequested        = null;
            NativeAudioController.OnUpdateQueueRequested = null;

            if (_playerListener != null && _player != null)
                _player.RemoveListener(_playerListener);

            _mediaSession?.Player?.Release();
            _mediaSession?.Release();
            _simpleCache?.Release();
            _simpleCache  = null;
            _databaseProvider = null;
            _mediaSession = null;
            Instance      = null;
            base.OnDestroy();
        }

        // ══════════════════════════════════════════════════════════
        // Listener nativo — completamente independiente del UI thread
        // ══════════════════════════════════════════════════════════
        private class PlayerListener : Java.Lang.Object, IPlayerListener
        {
            private readonly AndroidMedia3Service _svc;
            public PlayerListener(AndroidMedia3Service svc) => _svc = svc;

            // Se llama cuando ExoPlayer cambia de MediaItem (siguiente canción)
            public void OnMediaItemTransition(MediaItem? mediaItem, int reason)
            {
                // reason 1 = MEDIA_ITEM_TRANSITION_REASON_AUTO (canción terminó → siguiente automático)
                if (reason == 1)
                {
                    _svc._nextPrepared = false;
                    var id     = mediaItem?.MediaId ?? "";
                    var title  = mediaItem?.MediaMetadata?.Title?.ToString()      ?? "";
                    var artist = mediaItem?.MediaMetadata?.Artist?.ToString()     ?? "";
                    var thumb  = mediaItem?.MediaMetadata?.ArtworkUri?.ToString() ?? "";

                    Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                    {
                        NativeAudioController.ReportTrackStarted(id, title, artist, thumb, 0);
                    });

                    // Si la cola nativa tiene más, pre-fetch ya
                    if (_svc._nativeQueue.Count > 0 && !_svc._isFetchingNext)
                        _ = _svc.FetchNextTrackNativelyAsync();
                }
            }

            // Se llama cuando ExoPlayer llega al final de toda la cola (STATE_ENDED)
            public void OnPlaybackStateChanged(int state)
            {
                // STATE_ENDED = 4
                if (state == 4)
                {
                    if ((_svc._player?.MediaItemCount ?? 0) == 0 && _svc._nativeQueue.Count == 0)
                    {
                        Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                        {
                            NativeAudioController.ReportTrackEnded();
                        });
                    }
                }
            }
        }


        // ══════════════════════════════════════════════════════════
        // Callback de MediaSession — habilita TODOS los controles
        // en la notificación, pantalla de bloqueo y Android Auto
        // ══════════════════════════════════════════════════════════
        private class MediaSessionCallback : Java.Lang.Object, MediaSession.ICallback
        {
            public MediaSession.ConnectionResult OnConnect(MediaSession session, MediaSession.ControllerInfo controller)
            {
                // Exponer TODOS los comandos por defecto — habilita Play, Pause, Next, Previous en notificación y lockscreen
                return MediaSession.ConnectionResult.Accept(
                    MediaSession.ConnectionResult.DefaultSessionCommands,
                    MediaSession.ConnectionResult.DefaultPlayerCommands);
            }
        }
    }
}
