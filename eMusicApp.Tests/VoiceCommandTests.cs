using eMusicApp;

namespace eMusicApp.Tests;

/// <summary>
/// Tests para el flujo de comandos de voz: play, stop, next, previous, resume,
/// service resurrection, y la sesión completa de voz.
/// </summary>
public class VoiceCommandTests : IDisposable
{
    public VoiceCommandTests() => Cleanup();
    public void Dispose() => Cleanup();

    private static void Cleanup()
    {
        NativeAudioController.OnPlayRequested = null;
        NativeAudioController.OnPauseRequested = null;
        NativeAudioController.OnResumeRequested = null;
        NativeAudioController.OnSeekRequested = null;
        NativeAudioController.OnSkipToNext = null;
        NativeAudioController.OnSkipToPrevious = null;
        NativeAudioController.OnPlaybackStateChanged = null;
        NativeAudioController.PendingPlayRequest = null;
    }

    // ═══════════════════════════════════════════
    //  VOICE COMMAND: PLAY + SEARCH
    //  "Gemini, reproduce bon jovi en eMusicApp"
    // ═══════════════════════════════════════════

    [Fact]
    public void VoicePlay_ServiceAlive_DispatchesToNativeController()
    {
        string? playedVideoId = null;
        NativeAudioController.OnPlayRequested = (url, title, artist, thumb, videoId) =>
            playedVideoId = videoId;

        NativeAudioController.RequestPlay(
            "https://stream.example/audio.mp4",
            "Livin' On A Prayer",
            "Bon Jovi",
            "https://img.example/bonjovi.jpg",
            "lDK9QqIzhwk");

        Assert.Equal("lDK9QqIzhwk", playedVideoId);
    }

    [Fact]
    public void VoicePlay_ServiceDead_QueuesAndCanBeRecovered()
    {
        // Servicio muerto — simula cold start por Gemini
        NativeAudioController.RequestPlay(
            "https://stream.example/audio.mp4",
            "It's My Life",
            "Bon Jovi",
            "https://img.example/thumb.jpg",
            "vx2u5uUu3DE");

        // Verificar que se encoló
        Assert.NotNull(NativeAudioController.PendingPlayRequest);
        Assert.Equal("vx2u5uUu3DE", NativeAudioController.PendingPlayRequest!.Value.videoId);
        Assert.Equal("It's My Life", NativeAudioController.PendingPlayRequest!.Value.title);

        // Simular resurrección del servicio (lo que hace AndroidMedia3Service.OnCreate)
        string? flushedId = null;
        NativeAudioController.OnPlayRequested = (_, _, _, _, vid) => flushedId = vid;
        var p = NativeAudioController.PendingPlayRequest.Value;
        NativeAudioController.PendingPlayRequest = null;
        NativeAudioController.OnPlayRequested(p.url, p.title, p.artist, p.thumb, p.videoId);

        Assert.Equal("vx2u5uUu3DE", flushedId);
        Assert.Null(NativeAudioController.PendingPlayRequest);
    }

    // ═══════════════════════════════════════════
    //  VOICE COMMAND: STOP / PAUSE
    //  "Gemini, para la música"
    // ═══════════════════════════════════════════

    [Fact]
    public void VoiceStop_PausesPlayback()
    {
        bool paused = false;
        NativeAudioController.OnPauseRequested = () => paused = true;

        NativeAudioController.RequestPause();

        Assert.True(paused);
    }

    [Fact]
    public void VoiceStop_ServiceDead_DoesNotCrash()
    {
        var ex = Record.Exception(() => NativeAudioController.RequestPause());
        Assert.Null(ex);
    }

    // ═══════════════════════════════════════════
    //  VOICE COMMAND: NEXT
    //  "Gemini, siguiente canción"
    // ═══════════════════════════════════════════

    [Fact]
    public void VoiceNext_TriggersSkipToNext()
    {
        bool skipped = false;
        NativeAudioController.OnSkipToNext = () => skipped = true;

        // SkipAwareForwardingPlayer invoca este callback
        NativeAudioController.OnSkipToNext!.Invoke();

        Assert.True(skipped);
    }

    // ═══════════════════════════════════════════
    //  VOICE COMMAND: PREVIOUS
    //  "Gemini, canción anterior"
    // ═══════════════════════════════════════════

    [Fact]
    public void VoicePrevious_TriggersSkipToPrevious()
    {
        bool skipped = false;
        NativeAudioController.OnSkipToPrevious = () => skipped = true;

        NativeAudioController.OnSkipToPrevious!.Invoke();

        Assert.True(skipped);
    }

    // ═══════════════════════════════════════════
    //  VOICE COMMAND: RESUME
    //  "Gemini, continúa la música"
    // ═══════════════════════════════════════════

    [Fact]
    public void VoiceResume_ServiceAlive_ResumesPlayback()
    {
        bool resumed = false;
        NativeAudioController.OnResumeRequested = () => resumed = true;

        NativeAudioController.RequestResume();

        Assert.True(resumed);
    }

    [Fact]
    public void VoiceResume_ServiceDead_DoesNotCrash()
    {
        var ex = Record.Exception(() => NativeAudioController.RequestResume());
        Assert.Null(ex);
    }

    // ═══════════════════════════════════════════
    //  FULL VOICE SESSION
    //  play → pause → resume → next → prev → stop
    // ═══════════════════════════════════════════

    [Fact]
    public void FullVoiceSession_PlayPauseResumeNextPrevStop()
    {
        var log = new List<string>();

        NativeAudioController.OnPlayRequested = (_, title, _, _, _) => log.Add($"play:{title}");
        NativeAudioController.OnPauseRequested = () => log.Add("pause");
        NativeAudioController.OnResumeRequested = () => log.Add("resume");
        NativeAudioController.OnSkipToNext = () => log.Add("next");
        NativeAudioController.OnSkipToPrevious = () => log.Add("prev");

        // 1. "Gemini, reproduce Bon Jovi en eMusicApp"
        NativeAudioController.RequestPlay("url", "Livin' On A Prayer", "Bon Jovi", "thumb", "vid1");

        // 2. "Gemini, para"
        NativeAudioController.RequestPause();

        // 3. "Gemini, continúa"
        NativeAudioController.RequestResume();

        // 4. "Gemini, siguiente"
        NativeAudioController.OnSkipToNext!.Invoke();

        // 5. "Gemini, anterior"
        NativeAudioController.OnSkipToPrevious!.Invoke();

        // 6. "Gemini, para la música"
        NativeAudioController.RequestPause();

        Assert.Equal(6, log.Count);
        Assert.Equal("play:Livin' On A Prayer", log[0]);
        Assert.Equal("pause", log[1]);
        Assert.Equal("resume", log[2]);
        Assert.Equal("next", log[3]);
        Assert.Equal("prev", log[4]);
        Assert.Equal("pause", log[5]);
    }

    // ═══════════════════════════════════════════
    //  SERVICE RESURRECTION: play → die → play → resurrect
    // ═══════════════════════════════════════════

    [Fact]
    public void ServiceResurrection_FullScenario()
    {
        var log = new List<string>();

        // 1. Servicio vivo — reproduce normalmente
        NativeAudioController.OnPlayRequested = (_, title, _, _, _) => log.Add($"play:{title}");
        NativeAudioController.RequestPlay("url", "Song 1", "Artist", "thumb", "vid1");
        Assert.Single(log);
        Assert.Equal("play:Song 1", log[0]);

        // 2. Servicio muere (simular OnDestroy)
        NativeAudioController.OnPlayRequested = null;
        NativeAudioController.OnPauseRequested = null;
        NativeAudioController.OnResumeRequested = null;

        // 3. 20 minutos después... usuario intenta reproducir
        NativeAudioController.RequestPlay("url2", "Song 2", "Artist", "thumb", "vid2");
        Assert.Single(log); // No se reprodujo — se encoló
        Assert.NotNull(NativeAudioController.PendingPlayRequest);
        Assert.Equal("vid2", NativeAudioController.PendingPlayRequest!.Value.videoId);

        // 4. Servicio resucita (AndroidMedia3Service.OnCreate se ejecuta)
        NativeAudioController.OnPlayRequested = (_, title, _, _, _) => log.Add($"play:{title}");
        var pending = NativeAudioController.PendingPlayRequest!.Value;
        NativeAudioController.PendingPlayRequest = null;
        NativeAudioController.OnPlayRequested(pending.url, pending.title, pending.artist, pending.thumb, pending.videoId);

        // 5. Verificar que se reprodujo Song 2
        Assert.Equal(2, log.Count);
        Assert.Equal("play:Song 2", log[1]);
        Assert.Null(NativeAudioController.PendingPlayRequest);
    }

    // ═══════════════════════════════════════════
    //  SEEK via voice: "Gemini, adelanta 30 segundos"
    // ═══════════════════════════════════════════

    [Fact]
    public void VoiceSeek_SeeksToPosition()
    {
        int? seekPos = null;
        NativeAudioController.OnSeekRequested = pos => seekPos = pos;

        NativeAudioController.RequestSeek(30000); // 30s

        Assert.Equal(30000, seekPos);
    }

    [Fact]
    public void VoiceSeek_Zero_SeeksToBeginning()
    {
        int? seekPos = null;
        NativeAudioController.OnSeekRequested = pos => seekPos = pos;

        NativeAudioController.RequestSeek(0);

        Assert.Equal(0, seekPos);
    }

    // ═══════════════════════════════════════════
    //  EDGE CASES
    // ═══════════════════════════════════════════

    [Fact]
    public void MultiplePlayRequests_ServiceDead_OnlyLastIsQueued()
    {
        // Servicio muerto — múltiples requests rápidos
        NativeAudioController.RequestPlay("url1", "Song 1", "A1", "t1", "vid1");
        NativeAudioController.RequestPlay("url2", "Song 2", "A2", "t2", "vid2");
        NativeAudioController.RequestPlay("url3", "Song 3", "A3", "t3", "vid3");

        // Solo el último queda pendiente
        Assert.Equal("vid3", NativeAudioController.PendingPlayRequest!.Value.videoId);
        Assert.Equal("Song 3", NativeAudioController.PendingPlayRequest!.Value.title);
    }

    [Fact]
    public void PlaybackState_TogglePlayPause_Sequence()
    {
        var states = new List<bool>();
        NativeAudioController.OnPlaybackStateChanged = isPlaying => states.Add(isPlaying);

        NativeAudioController.ReportPlaybackState(true);   // playing
        NativeAudioController.ReportPlaybackState(false);  // paused
        NativeAudioController.ReportPlaybackState(true);   // resumed

        Assert.Equal(3, states.Count);
        Assert.True(states[0]);
        Assert.False(states[1]);
        Assert.True(states[2]);
    }

    [Fact]
    public void AllCallbacks_Null_NothingCrashes()
    {
        // Todos los callbacks son null — ninguna llamada debería explotar
        Cleanup();

        var ex = Record.Exception(() =>
        {
            NativeAudioController.RequestPause();
            NativeAudioController.RequestSeek(5000);
            NativeAudioController.ReportProgress(1000, 5000);
            NativeAudioController.ReportPlaybackState(true);
            NativeAudioController.ReportBufferingState(false);
            NativeAudioController.ReportTrackEnded();
            NativeAudioController.ReportTrackStarted("id", "t", "a", "th", 100);
            NativeAudioController.ReportCrossfadeCompleted("t", "a", "th");
        });

        Assert.Null(ex);
    }
}
