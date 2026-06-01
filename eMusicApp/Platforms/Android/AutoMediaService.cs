using Android.App;
using Android.Content;
using Android.Media;
using Android.OS;
using Android.Support.V4.Media;
using Android.Support.V4.Media.Session;
using AndroidX.Core.App;
using AndroidX.Media;
using System.Collections.Generic;
using System;
using System.Net.Http;

namespace eMusicApp.Platforms.Android
{
    [Service(Exported = true, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMediaPlayback)]
    [IntentFilter(new[] { "android.media.browse.MediaBrowserService" })]
    public class AutoMediaService : MediaBrowserServiceCompat, MediaPlayer.IOnPreparedListener, MediaPlayer.IOnCompletionListener, MediaPlayer.IOnErrorListener, AudioManager.IOnAudioFocusChangeListener
    {
        private MediaSessionCompat? _mediaSession;
        private PlaybackStateCompat.Builder? _stateBuilder;
        private MediaPlayer? _mediaPlayer;
        private string? _currentUrl;
        private string? _currentTitle = "eMusic";
        private string? _currentArtist = "Cargando...";
        private global::Android.Graphics.Bitmap? _currentArtBitmap = null;
        private static readonly HttpClient _httpClient = new HttpClient();
        private System.Timers.Timer? _progressTimer;
        private bool _pausedByAudioFocus = false;
        
        private MediaPlayer? _nextMediaPlayer;
        private string? _nextUrl;
        private string? _nextTitle;
        private string? _nextArtist;
        private string? _nextThumbUrl;
        private bool _nextPrepared = false;
        
        private const int NOTIFICATION_ID = 1001;
        private const string CHANNEL_ID = "emusic_media_channel";

        public override void OnCreate()
        {
            base.OnCreate();

            _progressTimer = new System.Timers.Timer(1000); // 1 second
            _progressTimer.Elapsed += (s, e) => {
                if (_mediaPlayer != null && _mediaPlayer.IsPlaying) {
                    NativeAudioController.ReportProgress(_mediaPlayer.CurrentPosition, _mediaPlayer.Duration);
                }
            };

            _mediaSession = new MediaSessionCompat(this, "AutoMediaService");
            _mediaSession.SetFlags(MediaSessionCompat.FlagHandlesMediaButtons | MediaSessionCompat.FlagHandlesTransportControls);

            _stateBuilder = new PlaybackStateCompat.Builder()
                .SetActions(PlaybackStateCompat.ActionPlay | PlaybackStateCompat.ActionPause | PlaybackStateCompat.ActionStop | PlaybackStateCompat.ActionSkipToNext | PlaybackStateCompat.ActionSkipToPrevious | PlaybackStateCompat.ActionPlayFromSearch);
            
            _mediaSession.SetPlaybackState(_stateBuilder.Build());
            _mediaSession.SetCallback(new MediaSessionCallback(this));
            _mediaSession.Active = true;
            
            SessionToken = _mediaSession.SessionToken;
            
            CreateNotificationChannel();

            // Subscribe to cross-platform bridge
            NativeAudioController.OnPlayRequested = (url, title, artist, thumb) => {
                _currentUrl = url;
                _currentTitle = string.IsNullOrEmpty(title) ? "eMusic" : title;
                _currentArtist = string.IsNullOrEmpty(artist) ? "Reproduciendo..." : artist;
                _currentArtBitmap = null;
                
                UpdateMetadata(_currentTitle, _currentArtist, thumb);
                PlayAudio(url);
            };
            
            NativeAudioController.OnPauseRequested = () => {
                PauseAudio();
            };
            
            NativeAudioController.OnResumeRequested = () => {
                Logger.Log("NativeAudioController requested Resume.");
                ResumeAudio();
            };

            NativeAudioController.OnSeekRequested = (positionMs) => {
                if (_mediaPlayer != null)
                {
                    _mediaPlayer.SeekTo(positionMs);
                }
            };

            NativeAudioController.OnPrepareNextRequested = (url, title, artist, thumb) => {
                PrepareNextAudio(url, title, artist, thumb);
            };

            NativeAudioController.OnStartCrossfadeRequested = () => {
                StartCrossfade();
            };

            Logger.Log("AutoMediaService OnCreate completed.");
        }

        public void PlayAudio(string url)
        {
            Logger.Log($"PlayAudio called with URL: {url}");
            try 
            {
                var audioManager = (AudioManager?)GetSystemService(AudioService);
                audioManager?.RequestAudioFocus(this, global::Android.Media.Stream.Music, AudioFocus.Gain);

                if (_mediaPlayer != null)
                {
                    _mediaPlayer.Stop();
                    _mediaPlayer.Release();
                    _mediaPlayer = null;
                }

                _mediaPlayer = new MediaPlayer();
                _mediaPlayer.SetAudioAttributes(new AudioAttributes.Builder()
                    ?.SetUsage(AudioUsageKind.Media)
                    ?.SetContentType(AudioContentType.Music)
                    ?.Build());

                _mediaPlayer.SetWakeMode(this, WakeLockFlags.Partial);

                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" }
                };

                _mediaPlayer.SetDataSource(ApplicationContext, global::Android.Net.Uri.Parse(url), headers);
                _mediaPlayer.SetOnPreparedListener(this);
                _mediaPlayer.SetOnCompletionListener(this);
                _mediaPlayer.SetOnErrorListener(this);
                _mediaPlayer.PrepareAsync(); // Use async to not block UI thread
                
                StartForegroundServiceWithNotification();
            }
            catch (System.Exception ex)
            {
                Logger.Log("Error playing audio inside PlayAudio", ex);
                System.Console.WriteLine("Error playing audio: " + ex.Message);
            }
        }

        public void PauseAudio()
        {
            Logger.Log("PauseAudio called.");
            if (_mediaPlayer != null && _mediaPlayer.IsPlaying)
            {
                _mediaPlayer.Pause();
                if (_nextMediaPlayer != null && _nextMediaPlayer.IsPlaying) _nextMediaPlayer.Pause();
                UpdatePlaybackState(PlaybackStateCompat.StatePaused);
                _progressTimer?.Stop();
                StartForegroundServiceWithNotification(); // Update notification button to Play
                NativeAudioController.ReportPlaybackState(false);
            }
        }

        public void ResumeAudio()
        {
            Logger.Log("ResumeAudio called.");
            if (_mediaPlayer != null && !_mediaPlayer.IsPlaying)
            {
                var audioManager = (AudioManager?)GetSystemService(AudioService);
                audioManager?.RequestAudioFocus(this, global::Android.Media.Stream.Music, AudioFocus.Gain);

                _mediaPlayer.Start();
                // We don't blindly resume nextMediaPlayer unless it was mid-crossfade, 
                // but keeping it simple: just let it be. If crossfading, it will resume.
                UpdatePlaybackState(PlaybackStateCompat.StatePlaying);
                _progressTimer?.Start();
                StartForegroundServiceWithNotification(); // Update notification button to Pause
                NativeAudioController.ReportPlaybackState(true);
            }
        }

        public void PrepareNextAudio(string url, string title, string artist, string thumbUrl)
        {
            Logger.Log($"PrepareNextAudio called with URL: {url}");
            if (_nextMediaPlayer != null)
            {
                _nextMediaPlayer.Release();
                _nextMediaPlayer = null;
            }

            _nextUrl = url;
            _nextTitle = string.IsNullOrEmpty(title) ? "eMusic" : title;
            _nextArtist = string.IsNullOrEmpty(artist) ? "Reproduciendo..." : artist;
            _nextThumbUrl = thumbUrl;
            _nextPrepared = false;

            try
            {
                _nextMediaPlayer = new MediaPlayer();
                _nextMediaPlayer.SetAudioAttributes(new AudioAttributes.Builder()
                    ?.SetUsage(AudioUsageKind.Media)
                    ?.SetContentType(AudioContentType.Music)
                    ?.Build());
                _nextMediaPlayer.SetWakeMode(this, WakeLockFlags.Partial);

                var headers = new Dictionary<string, string>
                {
                    { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36" }
                };

                _nextMediaPlayer.SetDataSource(ApplicationContext, global::Android.Net.Uri.Parse(url), headers);
                _nextMediaPlayer.Prepared += (sender, e) => {
                    _nextPrepared = true;
                    Logger.Log("Next player prepared successfully");
                };
                _nextMediaPlayer.PrepareAsync();
            }
            catch (Exception ex)
            {
                Logger.Log("Error preparing next audio", ex);
            }
        }

        public async void StartCrossfade()
        {
            if (_nextMediaPlayer == null || !_nextPrepared) 
            {
                // If not prepared in time, just skip crossfade and report track ended so React handles it normally
                NativeAudioController.ReportTrackEnded();
                return;
            }

            Logger.Log("Starting Crossfade");
            try
            {
                _nextMediaPlayer.SetVolume(0f, 0f);
                _nextMediaPlayer.Start();

                int steps = 30;
                int delayMs = 100; // 3000ms total fade

                for (int i = 0; i <= steps; i++)
                {
                    if (_mediaPlayer == null || _nextMediaPlayer == null) break;
                    
                    float volumeNext = i / (float)steps;
                    float volumeCurrent = 1f - volumeNext;
                    
                    _mediaPlayer.SetVolume(volumeCurrent, volumeCurrent);
                    _nextMediaPlayer.SetVolume(volumeNext, volumeNext);
                    
                    await System.Threading.Tasks.Task.Delay(delayMs);
                }

                // Swap players
                var oldPlayer = _mediaPlayer;
                _mediaPlayer = _nextMediaPlayer;
                _nextMediaPlayer = null;
                _nextPrepared = false;

                if (oldPlayer != null)
                {
                    oldPlayer.Stop();
                    oldPlayer.Release();
                }

                _mediaPlayer?.SetVolume(1.0f, 1.0f);
                _mediaPlayer?.SetOnPreparedListener(this);
                _mediaPlayer?.SetOnCompletionListener(this);
                _mediaPlayer?.SetOnErrorListener(this);

                _currentUrl = _nextUrl;
                _currentTitle = _nextTitle;
                _currentArtist = _nextArtist;
                
                UpdateMetadata(_currentTitle, _currentArtist, _nextThumbUrl);
            }
            catch (Exception ex)
            {
                Logger.Log("Error in crossfade", ex);
            }
        }

        public void OnPrepared(MediaPlayer? mp)
        {
            Logger.Log("MediaPlayer OnPrepared.");
            mp?.Start();
            UpdatePlaybackState(PlaybackStateCompat.StatePlaying);
            _progressTimer?.Start();
        }

        public bool OnError(MediaPlayer? mp, MediaError what, int extra)
        {
            Logger.Log($"MediaPlayer OnError: what={what} extra={extra}");
            Console.WriteLine($"MediaPlayer Error: {what} Extra: {extra}");
            NativeAudioController.ReportTrackEnded();
            return true;
        }

        public void OnCompletion(MediaPlayer? mp)
        {
            Logger.Log("MediaPlayer OnCompletion.");
            UpdatePlaybackState(PlaybackStateCompat.StateStopped);
            _progressTimer?.Stop();
            
            // Acquire a temporary WakeLock to give WebView time to fetch the next track
            try {
                var powerManager = (PowerManager?)GetSystemService(PowerService);
                var wakeLock = powerManager?.NewWakeLock(WakeLockFlags.Partial, "eMusic:AutoPlayNext");
                wakeLock?.Acquire(15000); // Hold for 15 seconds max
            } catch (Exception ex) {
                Logger.Log("Error acquiring WakeLock", ex);
            }
            
            NativeAudioController.ReportTrackEnded();
        }

        public void OnAudioFocusChange(AudioFocus focusChange)
        {
            Logger.Log($"OnAudioFocusChange: {focusChange}");
            if (_mediaPlayer == null) return;

            switch (focusChange)
            {
                case AudioFocus.Loss:
                    _pausedByAudioFocus = false;
                    PauseAudio();
                    break;
                case AudioFocus.LossTransient:
                    if (_mediaPlayer.IsPlaying)
                    {
                        _pausedByAudioFocus = true;
                        PauseAudio();
                    }
                    break;
                case AudioFocus.LossTransientCanDuck:
                    if (_mediaPlayer.IsPlaying)
                    {
                        // Maps/Navigation speaking: user requested pausing instead of ducking
                        _pausedByAudioFocus = true;
                        PauseAudio();
                    }
                    break;
                case AudioFocus.Gain:
                    // Restore volume just in case it was ducked
                    _mediaPlayer.SetVolume(1.0f, 1.0f);
                    if (_pausedByAudioFocus)
                    {
                        _pausedByAudioFocus = false;
                        ResumeAudio();
                    }
                    break;
            }
        }
        
        private void UpdatePlaybackState(int state)
        {
            _stateBuilder?.SetState(state, _mediaPlayer?.CurrentPosition ?? 0, 1.0f);
            _mediaSession?.SetPlaybackState(_stateBuilder?.Build());
        }

        private async void UpdateMetadata(string title, string artist, string thumbUrl)
        {
            var builder = new MediaMetadataCompat.Builder()
                .PutString(MediaMetadataCompat.MetadataKeyTitle, title)
                .PutString(MediaMetadataCompat.MetadataKeyArtist, artist);
                
            if (!string.IsNullOrEmpty(thumbUrl))
            {
                builder.PutString(MediaMetadataCompat.MetadataKeyAlbumArtUri, thumbUrl);
                builder.PutString(MediaMetadataCompat.MetadataKeyDisplayIconUri, thumbUrl);
                
                try {
                    var imageBytes = await _httpClient.GetByteArrayAsync(thumbUrl);
                    _currentArtBitmap = global::Android.Graphics.BitmapFactory.DecodeByteArray(imageBytes, 0, imageBytes.Length);
                    
                    if (_currentArtBitmap != null) {
                        builder.PutBitmap(MediaMetadataCompat.MetadataKeyAlbumArt, _currentArtBitmap);
                        builder.PutBitmap(MediaMetadataCompat.MetadataKeyDisplayIcon, _currentArtBitmap);
                    }
                } catch (Exception ex) {
                    Logger.Log("Failed to download album art bitmap", ex);
                    _currentArtBitmap = null;
                }
            }
            else {
                _currentArtBitmap = null;
            }
            
            _mediaSession?.SetMetadata(builder.Build());
            
            if (_mediaPlayer != null) {
                StartForegroundServiceWithNotification();
            }
        }

        private void CreateNotificationChannel()
        {
            if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
            {
                var channel = new NotificationChannel(CHANNEL_ID, "eMusic Playback", NotificationImportance.Low);
                channel.Description = "Music playback controls";
                var notificationManager = (NotificationManager?)GetSystemService(NotificationService);
                notificationManager?.CreateNotificationChannel(channel);
            }
        }

        private void StartForegroundServiceWithNotification()
        {
            var playIntent = AndroidX.Media.Session.MediaButtonReceiver.BuildMediaButtonPendingIntent(this, PlaybackStateCompat.ActionPlay);
            var pauseIntent = AndroidX.Media.Session.MediaButtonReceiver.BuildMediaButtonPendingIntent(this, PlaybackStateCompat.ActionPause);
            var nextIntent = AndroidX.Media.Session.MediaButtonReceiver.BuildMediaButtonPendingIntent(this, PlaybackStateCompat.ActionSkipToNext);
            var prevIntent = AndroidX.Media.Session.MediaButtonReceiver.BuildMediaButtonPendingIntent(this, PlaybackStateCompat.ActionSkipToPrevious);

            bool isPlaying = _mediaPlayer != null && _mediaPlayer.IsPlaying;

            var notificationBuilder = new NotificationCompat.Builder(this, CHANNEL_ID)
                .SetContentTitle(_currentTitle)
                .SetContentText(_currentArtist)
                .SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay) // fallback icon
                .SetLargeIcon(_currentArtBitmap)
                .SetVisibility(NotificationCompat.VisibilityPublic)
                .SetOngoing(true)
                .AddAction(new NotificationCompat.Action(global::Android.Resource.Drawable.IcMediaPrevious, "Anterior", prevIntent))
                .AddAction(new NotificationCompat.Action(isPlaying ? global::Android.Resource.Drawable.IcMediaPause : global::Android.Resource.Drawable.IcMediaPlay, isPlaying ? "Pausa" : "Play", isPlaying ? pauseIntent : playIntent))
                .AddAction(new NotificationCompat.Action(global::Android.Resource.Drawable.IcMediaNext, "Siguiente", nextIntent))
                .SetStyle(new AndroidX.Media.App.NotificationCompat.MediaStyle()
                    .SetMediaSession(_mediaSession?.SessionToken)
                    .SetShowActionsInCompactView(0, 1, 2));

            var notification = notificationBuilder.Build();
            
            if (Build.VERSION.SdkInt >= BuildVersionCodes.Q)
            {
                StartForeground(NOTIFICATION_ID, notification, global::Android.Content.PM.ForegroundService.TypeMediaPlayback);
            }
            else
            {
                StartForeground(NOTIFICATION_ID, notification);
            }
        }

        public override BrowserRoot OnGetRoot(string clientPackageName, int clientUid, Bundle? rootHints)
        {
            return new BrowserRoot("root", null);
        }

        public override void OnLoadChildren(string parentId, Result result)
        {
            if (parentId == "root")
            {
                result.Detach();
                System.Threading.Tasks.Task.Run(async () => {
                    var mediaItems = await PipedClient.GetTrendingAsync();
                    var javaList = new Java.Util.ArrayList();
                    foreach (var item in mediaItems) {
                        javaList.Add(item);
                    }
                    result.SendResult(javaList);
                });
            }
            else
            {
                result.SendResult(new Java.Util.ArrayList());
            }
        }

        public override void OnDestroy()
        {
            Logger.Log("AutoMediaService OnDestroy.");
            
            var audioManager = (AudioManager?)GetSystemService(AudioService);
            audioManager?.AbandonAudioFocus(this);

            base.OnDestroy();
            _progressTimer?.Stop();
            _progressTimer?.Dispose();
            _mediaSession?.Release();
            if (_mediaPlayer != null)
            {
                _mediaPlayer.Release();
                _mediaPlayer = null;
            }
            if (_nextMediaPlayer != null)
            {
                _nextMediaPlayer.Release();
                _nextMediaPlayer = null;
            }
        }

        private class MediaSessionCallback : MediaSessionCompat.Callback
        {
            private AutoMediaService _service;
            public MediaSessionCallback(AutoMediaService service)
            {
                _service = service;
            }

            public override void OnPlay()
            {
                Logger.Log("MediaSession OnPlay");
                _service.ResumeAudio();
            }

            public override void OnPlayFromMediaId(string? mediaId, Bundle? extras)
            {
                Logger.Log($"MediaSession OnPlayFromMediaId: {mediaId}");
                if (string.IsNullOrEmpty(mediaId)) return;
                
                System.Threading.Tasks.Task.Run(async () => {
                    try {
                        var streamUrl = await PipedClient.GetStreamUrlAsync(mediaId);
                        if (!string.IsNullOrEmpty(streamUrl))
                        {
                            new Handler(Looper.MainLooper).Post(() => {
                                _service.PlayAudio(streamUrl);
                            });
                        }
                        else {
                            Logger.Log("OnPlayFromMediaId: StreamUrl was null.");
                        }
                    } catch (Exception ex) {
                        Logger.Log("Error in OnPlayFromMediaId", ex);
                    }
                });
            }

            public override void OnPause()
            {
                _service.PauseAudio();
            }

            public override void OnStop()
            {
                _service.PauseAudio();
            }

            public override void OnSkipToNext()
            {
                NativeAudioController.OnSkipToNext?.Invoke();
            }

            public override void OnSkipToPrevious()
            {
                NativeAudioController.OnSkipToPrevious?.Invoke();
            }

            public override void OnPlayFromSearch(string? query, Bundle? extras)
            {
                if (!string.IsNullOrEmpty(query))
                {
                    NativeAudioController.OnSearchRequested?.Invoke(query);
                }
            }
        }
    }
}
