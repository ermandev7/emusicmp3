using eMusicApp;

namespace eMusicApp.Tests;

/// <summary>
/// Tests de estrés, edge cases, consistencia de estado, y escenarios
/// de uso real que pueden causar bugs en producción.
/// </summary>
public class StressAndEdgeCaseTests : IDisposable
{
    public StressAndEdgeCaseTests() => Cleanup();
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
    //  RAPID FIRE — simula taps rápidos del usuario
    // ═══════════════════════════════════════════

    [Fact]
    public void RapidFire_100Plays_ServiceAlive_AllDispatched()
    {
        int count = 0;
        NativeAudioController.OnPlayRequested = (_, _, _, _, _) => Interlocked.Increment(ref count);

        for (int i = 0; i < 100; i++)
            NativeAudioController.RequestPlay($"url{i}", $"Song{i}", "Artist", "thumb", $"vid{i}");

        Assert.Equal(100, count);
        Assert.Null(NativeAudioController.PendingPlayRequest);
    }

    [Fact]
    public void RapidFire_100Plays_ServiceDead_OnlyLastQueued()
    {
        for (int i = 0; i < 100; i++)
            NativeAudioController.RequestPlay($"url{i}", $"Song{i}", "Artist", "thumb", $"vid{i}");

        Assert.NotNull(NativeAudioController.PendingPlayRequest);
        Assert.Equal("vid99", NativeAudioController.PendingPlayRequest!.Value.videoId);
        Assert.Equal("Song99", NativeAudioController.PendingPlayRequest!.Value.title);
    }

    [Fact]
    public void RapidFire_PauseResumeCycle_50Times()
    {
        int pauseCount = 0, resumeCount = 0;
        NativeAudioController.OnPauseRequested = () => Interlocked.Increment(ref pauseCount);
        NativeAudioController.OnResumeRequested = () => Interlocked.Increment(ref resumeCount);

        for (int i = 0; i < 50; i++)
        {
            NativeAudioController.RequestPause();
            NativeAudioController.RequestResume();
        }

        Assert.Equal(50, pauseCount);
        Assert.Equal(50, resumeCount);
    }

    [Fact]
    public void RapidFire_Seek_ManyPositions()
    {
        var positions = new List<int>();
        NativeAudioController.OnSeekRequested = pos => positions.Add(pos);

        for (int i = 0; i < 50; i++)
            NativeAudioController.RequestSeek(i * 1000);

        Assert.Equal(50, positions.Count);
        Assert.Equal(0, positions[0]);
        Assert.Equal(49000, positions[49]);
    }

    [Fact]
    public void RapidFire_ProgressReports_1000Ticks()
    {
        int reportCount = 0;
        NativeAudioController.OnProgressUpdated = (_, _) => Interlocked.Increment(ref reportCount);

        for (int i = 0; i < 1000; i++)
            NativeAudioController.ReportProgress(i * 500, 300000);

        Assert.Equal(1000, reportCount);
    }

    // ═══════════════════════════════════════════
    //  EMPTY / NULL STRINGS — edge cases de metadata
    // ═══════════════════════════════════════════

    [Fact]
    public void Play_EmptyStrings_DoesNotCrash()
    {
        string? capturedUrl = null;
        NativeAudioController.OnPlayRequested = (url, _, _, _, _) => capturedUrl = url;

        var ex = Record.Exception(() =>
            NativeAudioController.RequestPlay("", "", "", "", ""));

        Assert.Null(ex);
        Assert.Equal("", capturedUrl);
    }

    [Fact]
    public void TrackStarted_EmptyMetadata_DoesNotCrash()
    {
        string? vid = null;
        NativeAudioController.OnTrackStarted = (v, _, _, _, _) => vid = v;

        var ex = Record.Exception(() =>
            NativeAudioController.ReportTrackStarted("", "", "", "", 0));

        Assert.Null(ex);
        Assert.Equal("", vid);
    }

    [Fact]
    public void CrossfadeCompleted_EmptyMetadata_DoesNotCrash()
    {
        var ex = Record.Exception(() =>
            NativeAudioController.ReportCrossfadeCompleted("", "", ""));
        Assert.Null(ex);
    }

    [Fact]
    public void Play_SpecialCharacters_PreservesExactly()
    {
        string? capturedTitle = null, capturedArtist = null;
        NativeAudioController.OnPlayRequested = (_, title, artist, _, _) =>
        {
            capturedTitle = title;
            capturedArtist = artist;
        };

        NativeAudioController.RequestPlay("url",
            "Livin' On A Prayer (Official Video) [HD]",
            "Bon Jovi & Friends ft. Somebody™",
            "thumb", "vid");

        Assert.Equal("Livin' On A Prayer (Official Video) [HD]", capturedTitle);
        Assert.Equal("Bon Jovi & Friends ft. Somebody™", capturedArtist);
    }

    [Fact]
    public void Play_UnicodeTitle_PreservesCorrectly()
    {
        string? capturedTitle = null;
        NativeAudioController.OnPlayRequested = (_, title, _, _, _) => capturedTitle = title;

        NativeAudioController.RequestPlay("url",
            "Despacito 🎵 — Luis Fonsi & Daddy Yankee (año 2017)",
            "Artist", "thumb", "vid");

        Assert.Contains("Despacito", capturedTitle);
        Assert.Contains("año", capturedTitle);
    }

    [Fact]
    public void Play_VeryLongTitle_PreservesAll()
    {
        string longTitle = new string('A', 5000);
        string? captured = null;
        NativeAudioController.OnPlayRequested = (_, title, _, _, _) => captured = title;

        NativeAudioController.RequestPlay("url", longTitle, "artist", "thumb", "vid");

        Assert.Equal(5000, captured!.Length);
    }

    // ═══════════════════════════════════════════
    //  QUEUE MANAGEMENT
    // ═══════════════════════════════════════════

    [Fact]
    public void Queue_Empty_DoesNotCrash()
    {
        List<string>? received = null;
        NativeAudioController.OnUpdateQueueRequested = ids => received = ids;

        NativeAudioController.RequestUpdateQueue(new List<string>());

        Assert.NotNull(received);
        Assert.Empty(received!);
    }

    [Fact]
    public void Queue_LargeQueue_100Items()
    {
        List<string>? received = null;
        NativeAudioController.OnUpdateQueueRequested = ids => received = ids;

        var ids = Enumerable.Range(0, 100).Select(i => $"vid{i}").ToList();
        NativeAudioController.RequestUpdateQueue(ids);

        Assert.Equal(100, received!.Count);
        Assert.Equal("vid0", received[0]);
        Assert.Equal("vid99", received[99]);
    }

    [Fact]
    public void Queue_ReplacePrevious_OnlyLastSent()
    {
        List<string>? received = null;
        NativeAudioController.OnUpdateQueueRequested = ids => received = ids;

        NativeAudioController.RequestUpdateQueue(new List<string> { "a", "b" });
        NativeAudioController.RequestUpdateQueue(new List<string> { "x", "y", "z" });

        Assert.Equal(3, received!.Count);
        Assert.Equal("x", received[0]);
    }

    [Fact]
    public void Queue_ServiceDead_DoesNotCrash()
    {
        var ex = Record.Exception(() =>
            NativeAudioController.RequestUpdateQueue(new List<string> { "vid1" }));
        Assert.Null(ex);
    }

    // ═══════════════════════════════════════════
    //  CROSSFADE CONFIG
    // ═══════════════════════════════════════════

    [Fact]
    public void CrossfadeDuration_SetAndGet()
    {
        NativeAudioController.CrossfadeDurationMs = 3000;
        Assert.Equal(3000, NativeAudioController.CrossfadeDurationMs);
    }

    [Fact]
    public void CrossfadeDuration_ZeroDisables()
    {
        NativeAudioController.CrossfadeDurationMs = 5000;
        NativeAudioController.CrossfadeDurationMs = 0;
        Assert.Equal(0, NativeAudioController.CrossfadeDurationMs);
    }

    [Fact]
    public void CrossfadeDuration_NegativeValue_Stores()
    {
        // No debería usarse negativo, pero no debe crashear
        NativeAudioController.CrossfadeDurationMs = -1;
        Assert.Equal(-1, NativeAudioController.CrossfadeDurationMs);
    }

    // ═══════════════════════════════════════════
    //  CALLBACK REPLACEMENT — simula reconexión de servicio
    // ═══════════════════════════════════════════

    [Fact]
    public void CallbackReplacement_NewCallbackReceivesNewEvents()
    {
        var log1 = new List<string>();
        var log2 = new List<string>();

        // Servicio 1
        NativeAudioController.OnPlayRequested = (_, title, _, _, _) => log1.Add(title);
        NativeAudioController.RequestPlay("url", "Song1", "A", "t", "v1");

        // Servicio muere y resucita con nuevos callbacks
        NativeAudioController.OnPlayRequested = (_, title, _, _, _) => log2.Add(title);
        NativeAudioController.RequestPlay("url", "Song2", "A", "t", "v2");

        Assert.Single(log1);
        Assert.Equal("Song1", log1[0]);
        Assert.Single(log2);
        Assert.Equal("Song2", log2[0]);
    }

    [Fact]
    public void CallbackReplacement_OldCallbackNotInvokedAfterReplacement()
    {
        bool oldCalled = false;
        NativeAudioController.OnPauseRequested = () => oldCalled = true;

        // Reemplazar
        bool newCalled = false;
        NativeAudioController.OnPauseRequested = () => newCalled = true;

        NativeAudioController.RequestPause();

        Assert.False(oldCalled);
        Assert.True(newCalled);
    }

    // ═══════════════════════════════════════════
    //  PROGRESS EDGE CASES
    // ═══════════════════════════════════════════

    [Fact]
    public void Progress_ZeroPosition_ZeroDuration()
    {
        int? pos = null, dur = null;
        NativeAudioController.OnProgressUpdated = (p, d) => { pos = p; dur = d; };

        NativeAudioController.ReportProgress(0, 0);

        Assert.Equal(0, pos);
        Assert.Equal(0, dur);
    }

    [Fact]
    public void Progress_PositionBeyondDuration_DoesNotCrash()
    {
        int? pos = null, dur = null;
        NativeAudioController.OnProgressUpdated = (p, d) => { pos = p; dur = d; };

        // ExoPlayer a veces reporta posición > duración brevemente
        NativeAudioController.ReportProgress(210500, 210000);

        Assert.Equal(210500, pos);
        Assert.Equal(210000, dur);
    }

    [Fact]
    public void Progress_NegativePosition_DoesNotCrash()
    {
        int? pos = null;
        NativeAudioController.OnProgressUpdated = (p, _) => pos = p;

        NativeAudioController.ReportProgress(-1, 100000);

        Assert.Equal(-1, pos);
    }

    [Fact]
    public void Progress_LargeValues_3HourTrack()
    {
        int? pos = null, dur = null;
        NativeAudioController.OnProgressUpdated = (p, d) => { pos = p; dur = d; };

        int threeHoursMs = 3 * 60 * 60 * 1000; // 10,800,000
        NativeAudioController.ReportProgress(threeHoursMs / 2, threeHoursMs);

        Assert.Equal(threeHoursMs / 2, pos);
        Assert.Equal(threeHoursMs, dur);
    }

    // ═══════════════════════════════════════════
    //  PENDING REQUEST STATE MACHINE
    // ═══════════════════════════════════════════

    [Fact]
    public void PendingRequest_InitiallyNull()
    {
        Assert.Null(NativeAudioController.PendingPlayRequest);
    }

    [Fact]
    public void PendingRequest_SetDirectly_Readable()
    {
        NativeAudioController.PendingPlayRequest = ("url", "title", "artist", "thumb", "vid");
        var p = NativeAudioController.PendingPlayRequest!.Value;

        Assert.Equal("url", p.url);
        Assert.Equal("title", p.title);
        Assert.Equal("artist", p.artist);
        Assert.Equal("thumb", p.thumb);
        Assert.Equal("vid", p.videoId);
    }

    [Fact]
    public void PendingRequest_ClearBySettingNull()
    {
        NativeAudioController.PendingPlayRequest = ("url", "title", "artist", "thumb", "vid");
        NativeAudioController.PendingPlayRequest = null;

        Assert.Null(NativeAudioController.PendingPlayRequest);
    }

    // ═══════════════════════════════════════════
    //  BUFFERING → PLAYING STATE TRANSITIONS
    // ═══════════════════════════════════════════

    [Fact]
    public void BufferingThenPlaying_CorrectTransition()
    {
        var bufferingStates = new List<bool>();
        var playbackStates = new List<bool>();

        NativeAudioController.OnBufferingChanged = b => bufferingStates.Add(b);
        NativeAudioController.OnPlaybackStateChanged = p => playbackStates.Add(p);

        // Típico flujo: buffering → ready → playing
        NativeAudioController.ReportBufferingState(true);   // empezó a cargar
        NativeAudioController.ReportBufferingState(false);  // terminó de cargar
        NativeAudioController.ReportPlaybackState(true);    // reproduciendo

        Assert.Equal(2, bufferingStates.Count);
        Assert.True(bufferingStates[0]);
        Assert.False(bufferingStates[1]);
        Assert.Single(playbackStates);
        Assert.True(playbackStates[0]);
    }

    [Fact]
    public void BufferingInterrupted_MultipleBufferingEvents()
    {
        var states = new List<bool>();
        NativeAudioController.OnBufferingChanged = b => states.Add(b);

        // Red inestable: buffering on/off varias veces
        NativeAudioController.ReportBufferingState(true);
        NativeAudioController.ReportBufferingState(false);
        NativeAudioController.ReportBufferingState(true);
        NativeAudioController.ReportBufferingState(false);

        Assert.Equal(4, states.Count);
    }

    // ═══════════════════════════════════════════
    //  TRACK END → NEXT TRACK FLOW
    // ═══════════════════════════════════════════

    [Fact]
    public void TrackEnd_ThenNewTrackStart_CorrectSequence()
    {
        var log = new List<string>();

        NativeAudioController.OnTrackEnded = () => log.Add("ended");
        NativeAudioController.OnTrackStarted = (vid, _, _, _, _) => log.Add($"started:{vid}");
        NativeAudioController.OnPlaybackStateChanged = p => log.Add(p ? "playing" : "paused");

        // Canción termina → autoplay siguiente
        NativeAudioController.ReportTrackEnded();
        NativeAudioController.ReportPlaybackState(false);  // breve pausa entre tracks
        NativeAudioController.ReportTrackStarted("next1", "Next Song", "Artist", "thumb", 200000);
        NativeAudioController.ReportPlaybackState(true);   // nueva canción reproduciendo

        Assert.Equal(4, log.Count);
        Assert.Equal("ended", log[0]);
        Assert.Equal("paused", log[1]);
        Assert.Equal("started:next1", log[2]);
        Assert.Equal("playing", log[3]);
    }

    // ═══════════════════════════════════════════
    //  CONCURRENT SAFETY (simula main thread + bg thread)
    // ═══════════════════════════════════════════

    [Fact]
    public async Task ConcurrentProgressReports_DoesNotCrash()
    {
        int count = 0;
        NativeAudioController.OnProgressUpdated = (_, _) => Interlocked.Increment(ref count);

        var tasks = Enumerable.Range(0, 10).Select(i =>
            Task.Run(() =>
            {
                for (int j = 0; j < 100; j++)
                    NativeAudioController.ReportProgress(j * 500, 300000);
            })).ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(1000, count);
    }

    [Fact]
    public async Task ConcurrentPlaybackStateChanges_DoesNotCrash()
    {
        int count = 0;
        NativeAudioController.OnPlaybackStateChanged = _ => Interlocked.Increment(ref count);

        var tasks = Enumerable.Range(0, 10).Select(i =>
            Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                    NativeAudioController.ReportPlaybackState(j % 2 == 0);
            })).ToArray();

        await Task.WhenAll(tasks);
        Assert.Equal(500, count);
    }
}
