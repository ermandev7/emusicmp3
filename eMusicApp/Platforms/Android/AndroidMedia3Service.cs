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

        // Notificación de foreground
        private const string CHANNEL_ID = "emusic_playback";
        private const int NOTIF_ID = 1001; // Mismo ID que DefaultMediaNotificationProvider

        public override void OnCreate()
        {
            base.OnCreate();
            Instance = this;

            // Crear canal de notificación (requerido Android 8+)
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

            // ── ExoPlayer con WakeLock de red ──
            _player = new ExoPlayerBuilder(this)
                .SetMediaSourceFactory(mediaSourceFactory)
                .SetWakeMode(C.WakeModeNetwork)
                .SetHandleAudioBecomingNoisy(true)
                .Build();

            // ── MediaSession (sin callback personalizado — Media3 gestiona los controles) ──
            _mediaSession = new MediaSession.Builder(this, _player).Build();

            // ── Timer de progreso (solo para UI) ──
            _progressTimer = new System.Timers.Timer(1000);
            _progressTimer.Elapsed += OnProgressTick;
            _progressTimer.Start();
        }

        private void CreateNotificationChannel()
        {
            if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
            {
                var nm = (NotificationManager)GetSystemService(NotificationService)!;
                if (nm.GetNotificationChannel(CHANNEL_ID) == null)
                {
                    var channel = new NotificationChannel(
                        CHANNEL_ID,
                        "eMusicApp - Reproducción",
                        NotificationImportance.Low)
                    {
                        Description = "Controles de reproducción de música"
                    };
                    channel.SetShowBadge(false);
                    nm.CreateNotificationChannel(channel);
                }
            }
        }

        // Promueve el servicio a foreground con una notificación inmediata.
        // Esto garantiza que los controles aparezcan en la pantalla de bloqueo y el panel.
        private void PromoteToForeground(string title, string artist)
        {
            try
            {
                Notification notif;
                if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
                {
                    notif = new Notification.Builder(this, CHANNEL_ID)
                        .SetContentTitle(title)
                        .SetContentText(artist)
                        .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
                        .SetOngoing(true)
                        .Build();
                }
                else
                {
#pragma warning disable CS0618
                    notif = new Notification.Builder(this)
                        .SetContentTitle(title)
                        .SetContentText(artist)
                        .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay)
                        .SetOngoing(true)
                        .Build();
#pragma warning restore CS0618
                }

                // Especificar el tipo de servicio foreground (requerido en Android 10+)
                if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.Q)
                    StartForeground(NOTIF_ID, notif, global::Android.Content.PM.ForegroundService.TypeMediaPlayback);
                else
                    StartForeground(NOTIF_ID, notif);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Notif] startForeground error: {ex.Message}");
            }
        }

        public override MediaSession? OnGetSession(MediaSession.ControllerInfo controllerInfo)
            => _mediaSession;

        // ── Timer tick — TODA lectura de ExoPlayer en el Main Thread ──
        private void OnProgressTick(object? sender, System.Timers.ElapsedEventArgs e)
        {
            Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
            {
                if (_player == null) return;

                var state        = _player.PlaybackState;
                bool isBuffering = (state == 2);
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

                    var playingId = _player.CurrentMediaItem?.MediaId;
                    if (!string.IsNullOrEmpty(playingId) && playingId != _currentMediaId)
                    {
                        _currentMediaId = playingId;
                        var title  = _player.CurrentMediaItem?.MediaMetadata?.Title?.ToString()      ?? "";
                        var artist = _player.CurrentMediaItem?.MediaMetadata?.Artist?.ToString()     ?? "";
                        var thumb  = _player.CurrentMediaItem?.MediaMetadata?.ArtworkUri?.ToString() ?? "";
                        NativeAudioController.ReportTrackStarted(playingId, title, artist, thumb, durMs);

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

        // ── Reproducir ──
        public void PlayStream(string url, string title, string artist, string thumbUrl, string videoId)
        {
            if (_player == null) return;

            // Promover a foreground ANTES de tocar ExoPlayer — garantiza notificación en Samsung
            PromoteToForeground(title, artist);

            _nextPrepared = false;
            _nativeQueue.Clear();

            _player.ClearMediaItems();
            _player.SetMediaItem(CreateMediaItem(url, title, artist, thumbUrl, videoId));
            _player.Prepare();
            _player.Play();

            // Poblar la cola autoplay desde los relatedStreams del track actual
            _ = FetchRelatedAndQueueNextAsync(videoId);
        }

        private async Task FetchRelatedAndQueueNextAsync(string videoId)
        {
            try
            {
                var response = await _httpClient.GetStringAsync(
                    $"http://emusicmp3.duckdns.org:5050/api/streams/{videoId}");
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;

                _nativeQueue.Clear();
                if (root.TryGetProperty("relatedStreams", out var related))
                {
                    foreach (var rel in related.EnumerateArray())
                    {
                        if (rel.TryGetProperty("videoId", out var vIdEl))
                        {
                            var vId = vIdEl.GetString();
                            if (!string.IsNullOrEmpty(vId)) _nativeQueue.Enqueue(vId);
                        }
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
                    $"http://emusicmp3.duckdns.org:5050/api/streams/{nextVideoId}");
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
                    System.Diagnostics.Debug.WriteLine($"[Autoplay] ✅ Siguiente: {title}");

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
    }
}
