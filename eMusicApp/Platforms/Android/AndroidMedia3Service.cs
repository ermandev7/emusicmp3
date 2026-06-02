using Android.App;
using Android.Content;
using AndroidX.Media3.Common;
using AndroidX.Media3.ExoPlayer;
using AndroidX.Media3.ExoPlayer.Source;
using AndroidX.Media3.DataSource;
using AndroidX.Media3.DataSource.Cache;
using AndroidX.Media3.Database;
using AndroidX.Media3.Session;
using Java.Util.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.IO;

namespace eMusicApp.Platforms.Android
{
    [Service(Exported = true, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback)]
    [IntentFilter(new[] { "androidx.media3.session.MediaSessionService" })]
    public class AndroidMedia3Service : MediaSessionService
    {
        private MediaSession? _mediaSession;
        private IExoPlayer? _player;

        // Instancia estática para acceso rápido y control puente desde NativeAudioController
        public static AndroidMedia3Service? Instance { get; private set; }

        private System.Timers.Timer? _progressTimer;

        // Variables de Autoplay / Gapless
        private Queue<string> _nativeQueue = new Queue<string>();
        private bool _isFetchingNext = false;
        private bool _nextPrepared = false;
        private string? _currentMediaId;
        private static readonly HttpClient _httpClient = new HttpClient();

        // Variables de Caché Nativa LRU
        private SimpleCache? _simpleCache;
        private StandaloneDatabaseProvider? _databaseProvider;

        public override void OnCreate()
        {
            base.OnCreate();
            Instance = this;

            // 1. Puente: Vinculamos los botones de la UI de MAUI directamente a este servicio
            NativeAudioController.OnPlayRequested = (url, title, artist, thumb, videoId) => PlayStream(url, title, artist, thumb, videoId);
            NativeAudioController.OnPauseRequested = Pause;
            NativeAudioController.OnResumeRequested = Resume;
            NativeAudioController.OnSeekRequested = (pos) => SeekTo(pos);
            NativeAudioController.OnUpdateQueueRequested = (videoIds) => {
                _nativeQueue.Clear();
                foreach (var id in videoIds) _nativeQueue.Enqueue(id);
                _nextPrepared = false; // Reset flag for gapless
            };

            // 0. Configurar la Caché LRU de 500MB en el sistema de archivos del usuario
            var cacheDir = new Java.IO.File(CacheDir, "emusic_audio_cache");
            _databaseProvider = new StandaloneDatabaseProvider(this);
            var evictor = new LeastRecentlyUsedCacheEvictor(500 * 1024 * 1024); // 500 MB max
            _simpleCache = new SimpleCache(cacheDir, evictor, _databaseProvider);

            // Fabricante HTTP -> Fabricante Caché
            var httpDataSourceFactory = new DefaultHttpDataSource.Factory().SetAllowCrossProtocolRedirects(true);
            var cacheDataSourceFactory = new CacheDataSource.Factory()
                .SetCache(_simpleCache)
                .SetUpstreamDataSourceFactory(httpDataSourceFactory)
                .SetFlags(CacheDataSource.FlagIgnoreCacheOnError);

            var mediaSourceFactory = new DefaultMediaSourceFactory(this)
                .SetDataSourceFactory(cacheDataSourceFactory);

            // 1. Inicializar ExoPlayer con optimizaciones de red, batería y Caché
            _player = new AndroidX.Media3.ExoPlayer.ExoPlayerBuilder(this)
                .SetMediaSourceFactory(mediaSourceFactory)
                // ExoPlayer maneja automáticamente los WakeLocks nativos con esta configuración
                .SetWakeMode(C.WakeModeNetwork)
                // Pausa automática al desconectar auriculares
                .SetHandleAudioBecomingNoisy(true) 
                .Build();

            // 2. Inicializar MediaSession (Gestiona la UI de la notificación y Lockscreen)
            _mediaSession = new MediaSession.Builder(this, _player).Build();

            // 3. Temporizador para actualizar la barra de progreso en la UI y el estado
            _progressTimer = new System.Timers.Timer(1000);
            _progressTimer.Elapsed += (s, e) =>
            {
                Microsoft.Maui.ApplicationModel.MainThread.BeginInvokeOnMainThread(() =>
                {
                    if (_player != null)
                    {
                        var state = _player.PlaybackState;
                        // STATE_IDLE=1, STATE_BUFFERING=2, STATE_READY=3, STATE_ENDED=4
                        // Durante BUFFERING no cambiamos IsPlaying para evitar que el botón
                        // vuelva a "▶️" mientras yt-dlp está extrayendo/cargando el audio.
                        bool isBuffering = (state == 2);
                        NativeAudioController.ReportBufferingState(isBuffering);

                        if (!isBuffering)
                        {
                            NativeAudioController.ReportPlaybackState(_player.IsPlaying);
                        }

                        if (_player.IsPlaying)
                        {
                            long dur = _player.Duration;
                            int durMs = dur < 0 ? 0 : (int)dur;
                            long pos = _player.CurrentPosition;
                            int posMs = pos < 0 ? 0 : (int)pos;

                            NativeAudioController.ReportProgress(posMs, durMs);
                            
                            // Gatillo para TrackStarted (Historial)
                            var playingId = _player.CurrentMediaItem?.MediaId;
                            if (!string.IsNullOrEmpty(playingId) && playingId != _currentMediaId)
                            {
                                _currentMediaId = playingId;
                                var title = _player.CurrentMediaItem?.MediaMetadata?.Title?.ToString() ?? "";
                                var artist = _player.CurrentMediaItem?.MediaMetadata?.Artist?.ToString() ?? "";
                                var thumb = _player.CurrentMediaItem?.MediaMetadata?.ArtworkUri?.ToString() ?? "";
                                NativeAudioController.ReportTrackStarted(playingId, title, artist, thumb, durMs);
                            }

                            // Gatillo de 15 segundos para Pre-fetching Gapless
                            if (durMs > 0 && (durMs - posMs) <= 15000)
                            {
                                if (!_isFetchingNext && !_nextPrepared && _nativeQueue.Count > 0)
                                {
                                    _ = FetchNextTrackNativelyAsync();
                                }
                            }
                        }
                        
                        // Comprobar si hay error de red o bloqueo
                        if (_player.PlayerError != null)
                        {
                            System.Diagnostics.Debug.WriteLine($"[ExoPlayer ERROR] {_player.PlayerError.ErrorCodeName}: {_player.PlayerError.Message}");
                            NativeAudioController.ReportPlaybackState(false);
                            NativeAudioController.ReportTrackEnded();
                            _player.ClearMediaItems();
                        }
                        
                        // Comprobar si terminó (STATE_ENDED = 4)
                        if (state == 4)
                        {
                            NativeAudioController.ReportTrackEnded();
                            _nextPrepared = false;
                        }
                    }
                });
            };
            _progressTimer.Start();
        }

        // Endpoint principal para que Android descubra la sesión
        public override MediaSession? OnGetSession(MediaSession.ControllerInfo controllerInfo)
        {
            return _mediaSession;
        }

        // --- Inyección de Audio ---
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
                .SetCustomCacheKey(videoId)
                .SetMediaMetadata(metadata)
                .Build();
        }

        private async Task FetchNextTrackNativelyAsync()
        {
            _isFetchingNext = true;
            try
            {
                var nextVideoId = _nativeQueue.Dequeue();
                
                // Llama al servidor local de la Raspberry Pi de forma silenciosa
                var response = await _httpClient.GetStringAsync($"http://emusicmp3.duckdns.org:5050/api/streams/{nextVideoId}");
                using var doc = JsonDocument.Parse(response);
                var root = doc.RootElement;
                
                var title = root.GetProperty("title").GetString() ?? "Unknown";
                var uploader = root.GetProperty("uploader").GetString() ?? "Unknown";
                var thumb = root.GetProperty("thumbnailUrl").GetString() ?? "";
                
                var audioStreams = root.GetProperty("audioStreams");
                if (audioStreams.GetArrayLength() > 0)
                {
                    var nextUrl = audioStreams[0].GetProperty("url").GetString();
                    
                    if (!string.IsNullOrEmpty(nextUrl) && _player != null)
                    {
                        // Inyectar el siguiente track a ExoPlayer para GAPLESS PLAYBACK
                        _player.AddMediaItem(CreateMediaItem(nextUrl, title, uploader, thumb, nextVideoId));
                        _nextPrepared = true;
                        
                        // AUTOPLAY INFINITO: Regenerar la cola nativa con los nuevos "relatedStreams"
                        var related = root.GetProperty("relatedStreams");
                        _nativeQueue.Clear();
                        foreach (var rel in related.EnumerateArray())
                        {
                            var vId = rel.GetProperty("videoId").GetString();
                            if (!string.IsNullOrEmpty(vId)) _nativeQueue.Enqueue(vId);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Error de red silencioso
                System.Diagnostics.Debug.WriteLine($"Error prefetching next track: {ex.Message}");
            }
            finally
            {
                _isFetchingNext = false;
            }
        }

        public void Pause() => _player?.Pause();
        public void Resume() => _player?.Play();
        public void SeekTo(long positionMs) => _player?.SeekTo(positionMs);
        
        public long CurrentPosition => _player?.CurrentPosition ?? 0;
        public long Duration => _player?.Duration ?? 0;
        public bool IsPlaying => _player?.IsPlaying ?? false;

        public override void OnDestroy()
        {
            NativeAudioController.OnPlayRequested = null;
            NativeAudioController.OnPauseRequested = null;
            NativeAudioController.OnResumeRequested = null;
            NativeAudioController.OnSeekRequested = null;
            NativeAudioController.OnUpdateQueueRequested = null;

            _mediaSession?.Player?.Release();
            _mediaSession?.Release();
            
            // Liberar Caché para evitar locks de DB
            _simpleCache?.Release();
            _simpleCache = null;
            _databaseProvider = null;
            
            _mediaSession = null;
            Instance = null;
            base.OnDestroy();
        }
    }
}
