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

        private global::Android.OS.Handler? _progressHandler;
        private global::Java.Lang.Runnable? _progressRunnable;

        // Cola nativa para autoplay infinito
        private Queue<string> _nativeQueue = new Queue<string>();
        private bool _isFetchingNext = false;
        private bool _nextPrepared = false;
        private string? _currentMediaId;
        private bool _trackEndedReported = false;

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

            _mediaSession = new MediaSession.Builder(this, _player).Build();

            // ── Delegar notificación a Media3 con nuestro canal ──
            // Media3 enlaza automáticamente la MediaSession → lock screen + botones + progreso funcionan
            SetMediaNotificationProvider(
                new DefaultMediaNotificationProvider.Builder(this)
                    .SetChannelId(CHANNEL_ID)
                    .SetChannelNameResourceId(Resource.String.app_name)
                    .SetNotificationId(NOTIFICATION_ID)
                    .Build());

            _progressHandler  = new global::Android.OS.Handler(global::Android.OS.Looper.MainLooper!);
            _progressRunnable = new global::Java.Lang.Runnable(OnProgressTick);
            _progressHandler.PostDelayed(_progressRunnable, 500);
        }

        public override MediaSession? OnGetSession(MediaSession.ControllerInfo controllerInfo)
            => _mediaSession;

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
                    var thumb  = _player.CurrentMediaItem?.MediaMetadata?.ArtworkUri?.ToString() ?? "";
                    NativeAudioController.ReportTrackStarted(playingId, title, artist, thumb, durMs);

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

            _player.ClearMediaItems();
            _player.SetMediaItem(CreateMediaItem(url, title, artist, thumbUrl, videoId));
            _player.Prepare();
            _player.Play();

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
                        var vId = ExtractVideoId(rel);
                        if (!string.IsNullOrEmpty(vId) && vId != videoId)
                            _nativeQueue.Enqueue(vId);
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

                    if (root.TryGetProperty("relatedStreams", out var related))
                    {
                        _nativeQueue.Clear();
                        foreach (var rel in related.EnumerateArray())
                        {
                            var vId = ExtractVideoId(rel);
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

        public void Pause()  => _player?.Pause();
        public void Resume() => _player?.Play();
        public void SeekTo(long positionMs) => _player?.SeekTo(positionMs);

        public long CurrentPosition => _player?.CurrentPosition ?? 0;
        public long Duration        => _player?.Duration        ?? 0;
        public bool IsPlaying       => _player?.IsPlaying       ?? false;

        private void CreateNotificationChannel()
        {
            if (global::Android.OS.Build.VERSION.SdkInt < global::Android.OS.BuildVersionCodes.O) return;

            var nm = (global::Android.App.NotificationManager)GetSystemService(NotificationService)!;
            if (nm.GetNotificationChannel(CHANNEL_ID) != null) return;

            var channel = new global::Android.App.NotificationChannel(
                CHANNEL_ID,
                "eMusicApp - Reproducción",
                global::Android.App.NotificationImportance.Low)
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
