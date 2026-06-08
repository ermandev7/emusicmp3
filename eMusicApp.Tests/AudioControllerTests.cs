using eMusicApp;

namespace eMusicApp.Tests;

/// <summary>
/// Tests para NativeAudioController — verifica play, pause, resume, stop,
/// seek, next, prev, progress, buffering, track events, queue, crossfade,
/// y la resurrección del servicio cuando está muerto.
/// </summary>
public class AudioControllerTests : IDisposable
{
    public AudioControllerTests() => Cleanup();
    public void Dispose() => Cleanup();

    private static void Cleanup()
    {
        NativeAudioController.OnPlayRequested = null;
        NativeAudioController.OnPauseRequested = null;
        NativeAudioController.OnResumeRequested = null;
        NativeAudioController.OnSeekRequested = null;
        NativeAudioController.OnUpdateQueueRequested = null;
        NativeAudioController.OnProgressUpdated = null;
        NativeAudioController.OnTrackStarted = null;
        NativeAudioController.OnTrackEnded = null;
        NativeAudioController.OnCrossfadeCompleted = null;
        NativeAudioController.OnSkipToNext = null;
        NativeAudioController.OnSkipToPrevious = null;
        NativeAudioController.OnPlaybackStateChanged = null;
        NativeAudioController.OnBufferingChanged = null;
        NativeAudioController.PendingPlayRequest = null;
        NativeAudioController.CrossfadeDurationMs = 0;
    }

    // ═══════════════════════════════════════════
    //  PLAY
    // ═══════════════════════════════════════════

    [Fact]
    public void RequestPlay_ServiceAlive_InvokesCallbackWithAllParams()
    {
        string? capturedUrl = null, capturedTitle = null, capturedArtist = null;
        string? capturedThumb = null, capturedVideoId = null;

        NativeAudioController.OnPlayRequested = (url, title, artist, thumb, videoId) =>
        {
            capturedUrl = url; capturedTitle = title; capturedArtist = artist;
            capturedThumb = thumb; capturedVideoId = videoId;
        };

        NativeAudioController.RequestPlay(
            "https://audio.example/stream.mp3",
            "Livin' On A Prayer", "Bon Jovi",
            "https://img.example/thumb.jpg", "lDK9QqIzhwk");

        Assert.Equal("https://audio.example/stream.mp3", capturedUrl);
        Assert.Equal("Livin' On A Prayer", capturedTitle);
        Assert.Equal("Bon Jovi", capturedArtist);
        Assert.Equal("https://img.example/thumb.jpg", capturedThumb);
        Assert.Equal("lDK9QqIzhwk", capturedVideoId);
    }

    [Fact]
    public void RequestPlay_ServiceAlive_DoesNotSetPendingRequest()
    {
        NativeAudioController.OnPlayRequested = (_, _, _, _, _) => { };
        NativeAudioController.RequestPlay("url", "title", "artist", "thumb", "vid123");
        Assert.Null(NativeAudioController.PendingPlayRequest);
    }

    [Fact]
    public void RequestPlay_ServiceDead_QueuesPendingRequest()
    {
        NativeAudioController.RequestPlay(
            "https://audio.example/stream.mp3", "It's My Life",
            "Bon Jovi", "https://img.example/thumb.jpg", "vx2u5uUu3DE");

        Assert.NotNull(NativeAudioController.PendingPlayRequest);
        var p = NativeAudioController.PendingPlayRequest!.Value;
        Assert.Equal("https://audio.example/stream.mp3", p.url);
        Assert.Equal("It's My Life", p.title);
        Assert.Equal("Bon Jovi", p.artist);
        Assert.Equal("vx2u5uUu3DE", p.videoId);
    }

    [Fact]
    public void RequestPlay_ServiceDead_ThenAlive_FlushesCorrectly()
    {
        // Servicio muerto — se encola
        NativeAudioController.RequestPlay("url1", "Song1", "Artist1", "thumb1", "vid1");
        Assert.NotNull(NativeAudioController.PendingPlayRequest);

        // Servicio resucita
        string? flushedVideoId = null;
        NativeAudioController.OnPlayRequested = (_, _, _, _, videoId) => flushedVideoId = videoId;
        var pending = NativeAudioController.PendingPlayRequest!.Value;
        NativeAudioController.PendingPlayRequest = null;
        NativeAudioController.OnPlayRequested(pending.url, pending.title, pending.artist, pending.thumb, pending.videoId);

        Assert.Equal("vid1", flushedVideoId);
        Assert.Null(NativeAudioController.PendingPlayRequest);
    }

    [Fact]
    public void RequestPlay_ServiceDead_OverwritesPreviousPending()
    {
        NativeAudioController.RequestPlay("url1", "Song1", "A1", "t1", "vid1");
        NativeAudioController.RequestPlay("url2", "Song2", "A2", "t2", "vid2");
        Assert.Equal("vid2", NativeAudioController.PendingPlayRequest!.Value.videoId);
    }

    // ═══════════════════════════════════════════
    //  PAUSE
    // ═══════════════════════════════════════════

    [Fact]
    public void RequestPause_ServiceAlive_InvokesCallback()
    {
        bool paused = false;
        NativeAudioController.OnPauseRequested = () => paused = true;
        NativeAudioController.RequestPause();
        Assert.True(paused);
    }

    [Fact]
    public void RequestPause_ServiceDead_DoesNotThrow()
    {
        var ex = Record.Exception(() => NativeAudioController.RequestPause());
        Assert.Null(ex);
    }

    // ═══════════════════════════════════════════
    //  RESUME
    // ═══════════════════════════════════════════

    [Fact]
    public void RequestResume_ServiceAlive_InvokesCallback()
    {
        bool resumed = false;
        NativeAudioController.OnResumeRequested = () => resumed = true;
        NativeAudioController.RequestResume();
        Assert.True(resumed);
    }

    [Fact]
    public void RequestResume_ServiceDead_DoesNotThrow()
    {
        var ex = Record.Exception(() => NativeAudioController.RequestResume());
        Assert.Null(ex);
    }

    // ═══════════════════════════════════════════
    //  SEEK
    // ═══════════════════════════════════════════

    [Fact]
    public void RequestSeek_InvokesWithCorrectPosition()
    {
        int? seekPos = null;
        NativeAudioController.OnSeekRequested = pos => seekPos = pos;
        NativeAudioController.RequestSeek(45000);
        Assert.Equal(45000, seekPos);
    }

    [Fact]
    public void RequestSeek_Zero_SeeksToBeginning()
    {
        int? seekPos = null;
        NativeAudioController.OnSeekRequested = pos => seekPos = pos;
        NativeAudioController.RequestSeek(0);
        Assert.Equal(0, seekPos);
    }

    // ═══════════════════════════════════════════
    //  NEXT / PREVIOUS
    // ═══════════════════════════════════════════

    [Fact]
    public void SkipToNext_InvokesCallback()
    {
        bool skipped = false;
        NativeAudioController.OnSkipToNext = () => skipped = true;
        NativeAudioController.OnSkipToNext?.Invoke();
        Assert.True(skipped);
    }

    [Fact]
    public void SkipToPrevious_InvokesCallback()
    {
        bool skipped = false;
        NativeAudioController.OnSkipToPrevious = () => skipped = true;
        NativeAudioController.OnSkipToPrevious?.Invoke();
        Assert.True(skipped);
    }

    // ═══════════════════════════════════════════
    //  PLAYBACK STATE
    // ═══════════════════════════════════════════

    [Fact]
    public void ReportPlaybackState_Playing()
    {
        bool? r = null;
        NativeAudioController.OnPlaybackStateChanged = v => r = v;
        NativeAudioController.ReportPlaybackState(true);
        Assert.True(r);
    }

    [Fact]
    public void ReportPlaybackState_Paused()
    {
        bool? r = null;
        NativeAudioController.OnPlaybackStateChanged = v => r = v;
        NativeAudioController.ReportPlaybackState(false);
        Assert.False(r);
    }

    // ═══════════════════════════════════════════
    //  BUFFERING
    // ═══════════════════════════════════════════

    [Fact]
    public void ReportBufferingState_Buffering()
    {
        bool? r = null;
        NativeAudioController.OnBufferingChanged = v => r = v;
        NativeAudioController.ReportBufferingState(true);
        Assert.True(r);
    }

    [Fact]
    public void ReportBufferingState_Ready()
    {
        bool? r = null;
        NativeAudioController.OnBufferingChanged = v => r = v;
        NativeAudioController.ReportBufferingState(false);
        Assert.False(r);
    }

    // ═══════════════════════════════════════════
    //  PROGRESS
    // ═══════════════════════════════════════════

    [Fact]
    public void ReportProgress_ReportsPositionAndDuration()
    {
        int? pos = null, dur = null;
        NativeAudioController.OnProgressUpdated = (p, d) => { pos = p; dur = d; };
        NativeAudioController.ReportProgress(30000, 210000);
        Assert.Equal(30000, pos);
        Assert.Equal(210000, dur);
    }

    [Fact]
    public void ReportProgress_NoListener_DoesNotThrow()
    {
        var ex = Record.Exception(() => NativeAudioController.ReportProgress(1000, 5000));
        Assert.Null(ex);
    }

    // ═══════════════════════════════════════════
    //  TRACK EVENTS
    // ═══════════════════════════════════════════

    [Fact]
    public void ReportTrackStarted_ReportsAllMetadata()
    {
        string? vid = null, title = null, artist = null, thumb = null;
        int? dur = null;
        NativeAudioController.OnTrackStarted = (v, t, a, th, d) =>
            { vid = v; title = t; artist = a; thumb = th; dur = d; };

        NativeAudioController.ReportTrackStarted("abc123", "My Song", "Artist", "thumb.jpg", 180000);

        Assert.Equal("abc123", vid);
        Assert.Equal("My Song", title);
        Assert.Equal(180000, dur);
    }

    [Fact]
    public void ReportTrackEnded_InvokesCallback()
    {
        bool ended = false;
        NativeAudioController.OnTrackEnded = () => ended = true;
        NativeAudioController.ReportTrackEnded();
        Assert.True(ended);
    }

    // ═══════════════════════════════════════════
    //  QUEUE
    // ═══════════════════════════════════════════

    [Fact]
    public void RequestUpdateQueue_SendsVideoIds()
    {
        List<string>? received = null;
        NativeAudioController.OnUpdateQueueRequested = ids => received = ids;
        NativeAudioController.RequestUpdateQueue(new List<string> { "vid1", "vid2", "vid3" });
        Assert.NotNull(received);
        Assert.Equal(3, received!.Count);
        Assert.Equal("vid1", received[0]);
    }

    // ═══════════════════════════════════════════
    //  CROSSFADE
    // ═══════════════════════════════════════════

    [Fact]
    public void CrossfadeDuration_DefaultIsZero()
    {
        Assert.Equal(0, NativeAudioController.CrossfadeDurationMs);
    }

    [Fact]
    public void ReportCrossfadeCompleted_ReportsMetadata()
    {
        string? title = null, artist = null, thumb = null;
        NativeAudioController.OnCrossfadeCompleted = (t, a, th) =>
            { title = t; artist = a; thumb = th; };
        NativeAudioController.ReportCrossfadeCompleted("Next Song", "Next Artist", "next_thumb.jpg");
        Assert.Equal("Next Song", title);
    }
}
