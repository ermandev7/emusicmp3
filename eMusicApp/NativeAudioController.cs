namespace eMusicApp;

public static class NativeAudioController
{
    // Crossfade duration in ms; 0 = disabled. Set from PlayerViewModel, read from AndroidMedia3Service.
    public static int CrossfadeDurationMs { get; set; } = 0;

    // Action that Android native code will subscribe to
    public static Action<string, string, string, string, string>? OnPlayRequested { get; set; } // url, title, artist, thumb, videoId
    public static Action? OnPauseRequested { get; set; }
    public static Action? OnResumeRequested { get; set; }
    public static Action<int>? OnSeekRequested { get; set; }
    public static Action<List<string>>? OnUpdateQueueRequested { get; set; }

    // Events from Android Native to WebView
    public static Action<int, int>? OnProgressUpdated { get; set; } // positionMs, durationMs
    public static Action<string, string, string, string, int>? OnTrackStarted { get; set; }
    public static Action? OnTrackEnded { get; set; }
    public static Action<string, string, string>? OnCrossfadeCompleted { get; set; }
    public static Action? OnSkipToNext { get; set; }
    public static Action? OnSkipToPrevious { get; set; }
    public static Action<bool>? OnPlaybackStateChanged { get; set; }
    public static Action<bool>? OnBufferingChanged { get; set; } // true = cargando/buffering

    // Pending play request — se guarda cuando el servicio está muerto y se ejecuta al resucitar
    internal static (string url, string title, string artist, string thumb, string videoId)? PendingPlayRequest { get; set; }

    // Called from WebView (MainPage)
    public static void RequestPlay(string url, string title, string artist, string thumb, string videoId)
    {
        if (OnPlayRequested != null)
        {
            OnPlayRequested.Invoke(url, title, artist, thumb, videoId);
        }
        else
        {
            // Servicio muerto — guardar request y resucitar el servicio
            System.Diagnostics.Debug.WriteLine("[NativeAudioController] Service dead, queuing play and restarting...");
            PendingPlayRequest = (url, title, artist, thumb, videoId);
#if ANDROID
            try
            {
                var context = Android.App.Application.Context;
                var intent = new Android.Content.Intent(context, typeof(Platforms.Android.AndroidMedia3Service));
                context.StartForegroundService(intent);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeAudioController] Failed to restart service: {ex.Message}");
            }
#endif
        }
    }

    public static void RequestPause()
    {
        OnPauseRequested?.Invoke();
    }

    public static void RequestResume()
    {
        if (OnResumeRequested != null)
        {
            OnResumeRequested.Invoke();
        }
        else
        {
            // Servicio muerto — intentar resucitar (sin pending play, solo restart)
            System.Diagnostics.Debug.WriteLine("[NativeAudioController] Resume: service dead, restarting...");
#if ANDROID
            try
            {
                var context = Android.App.Application.Context;
                var intent = new Android.Content.Intent(context, typeof(Platforms.Android.AndroidMedia3Service));
                context.StartForegroundService(intent);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[NativeAudioController] Failed to restart service: {ex.Message}");
            }
#endif
        }
    }

    public static void RequestSeek(int positionMs)
    {
        OnSeekRequested?.Invoke(positionMs);
    }

    public static void RequestUpdateQueue(List<string> videoIds)
    {
        OnUpdateQueueRequested?.Invoke(videoIds);
    }

    // Called from Native Service
    public static void ReportProgress(int positionMs, int durationMs)
    {
        OnProgressUpdated?.Invoke(positionMs, durationMs);
    }

    public static void ReportTrackStarted(string videoId, string title, string artist, string thumb, int duration)
    {
        OnTrackStarted?.Invoke(videoId, title, artist, thumb, duration);
    }

    public static void ReportTrackEnded()
    {
        OnTrackEnded?.Invoke();
    }

    public static void ReportCrossfadeCompleted(string title, string artist, string thumb)
    {
        OnCrossfadeCompleted?.Invoke(title, artist, thumb);
    }

    public static void ReportPlaybackState(bool isPlaying)
    {
        OnPlaybackStateChanged?.Invoke(isPlaying);
    }

    public static void ReportBufferingState(bool isBuffering)
    {
        OnBufferingChanged?.Invoke(isBuffering);
    }
}
