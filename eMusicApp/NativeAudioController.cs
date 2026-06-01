namespace eMusicApp;

public static class NativeAudioController
{
    // Action that Android native code will subscribe to
    public static Action<string, string, string, string>? OnPlayRequested { get; set; } // url, title, artist, thumb
    public static Action<string, string, string, string>? OnPrepareNextRequested { get; set; }
    public static Action? OnStartCrossfadeRequested { get; set; }
    public static Action? OnPauseRequested { get; set; }
    public static Action? OnResumeRequested { get; set; }
    public static Action<int>? OnSeekRequested { get; set; }

    // Events from Android Native to WebView
    public static Action<int, int>? OnProgressUpdated { get; set; } // positionMs, durationMs
    public static Action? OnTrackEnded { get; set; }
    public static Action? OnSkipToNext { get; set; }
    public static Action? OnSkipToPrevious { get; set; }
    public static Action<string>? OnSearchRequested { get; set; }
    public static Action<bool>? OnPlaybackStateChanged { get; set; }

    // Called from WebView (MainPage)
    public static void RequestPlay(string url, string title, string artist, string thumb)
    {
        OnPlayRequested?.Invoke(url, title, artist, thumb);
    }

    public static void RequestPrepareNext(string url, string title, string artist, string thumb)
    {
        OnPrepareNextRequested?.Invoke(url, title, artist, thumb);
    }

    public static void RequestStartCrossfade()
    {
        OnStartCrossfadeRequested?.Invoke();
    }

    public static void RequestPause()
    {
        OnPauseRequested?.Invoke();
    }

    public static void RequestResume()
    {
        OnResumeRequested?.Invoke();
    }

    public static void RequestSeek(int positionMs)
    {
        OnSeekRequested?.Invoke(positionMs);
    }

    // Called from Native Service
    public static void ReportProgress(int positionMs, int durationMs)
    {
        OnProgressUpdated?.Invoke(positionMs, durationMs);
    }

    public static void ReportTrackEnded()
    {
        OnTrackEnded?.Invoke();
    }

    public static void ReportPlaybackState(bool isPlaying)
    {
        OnPlaybackStateChanged?.Invoke(isPlaying);
    }
}
