using eMusicApp.Services;

namespace eMusicApp.Tests;

public class VoiceParserTests
{
    // ═══════════════════════════════════════════
    //  WAKE WORD DETECTION
    // ═══════════════════════════════════════════

    [Theory]
    [InlineData("asistente reproduce salsa", "reproduce salsa")]
    [InlineData("gemini pon rock", "pon rock")]
    [InlineData("oye música siguiente", "siguiente")]
    [InlineData("hey music play bachata", "play bachata")]
    [InlineData("ok music para la música", "para la música")]
    public void WakeWord_Detected_ReturnsTextAfter(string input, string expected)
    {
        var result = VoiceCommandParser.ExtractAfterWakeWord(input);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("reproduce salsa")]
    [InlineData("hola qué tal")]
    [InlineData("")]
    public void WakeWord_NotDetected_ReturnsNull(string input)
    {
        Assert.Null(VoiceCommandParser.ExtractAfterWakeWord(input));
    }

    [Fact]
    public void WakeWord_AtEnd_ReturnsEmptyString()
    {
        var result = VoiceCommandParser.ExtractAfterWakeWord("hola asistente");
        Assert.Equal("", result);
    }

    // ═══════════════════════════════════════════
    //  CONTROL COMMANDS (no query)
    // ═══════════════════════════════════════════

    [Theory]
    [InlineData("para la música", VoiceAction.Stop)]
    [InlineData("detén", VoiceAction.Stop)]
    [InlineData("stop", VoiceAction.Stop)]
    [InlineData("pausa", VoiceAction.Pause)]
    [InlineData("pausar la canción", VoiceAction.Pause)]
    [InlineData("continúa", VoiceAction.Resume)]
    [InlineData("sigue la música", VoiceAction.Resume)]
    [InlineData("siguiente", VoiceAction.Next)]
    [InlineData("skip", VoiceAction.Next)]
    [InlineData("anterior", VoiceAction.Previous)]
    [InlineData("canción anterior", VoiceAction.Previous)]
    public void ControlCommands_ParsedCorrectly(string input, VoiceAction expected)
    {
        var cmd = VoiceCommandParser.Parse(input);
        Assert.Equal(expected, cmd.Action);
    }

    // ═══════════════════════════════════════════
    //  PLAY / SEARCH COMMANDS
    // ═══════════════════════════════════════════

    [Theory]
    [InlineData("reproduce salsa", VoiceAction.Play, "salsa")]
    [InlineData("pon reggaeton", VoiceAction.Play, "reggaeton")]
    [InlineData("play rock clásico", VoiceAction.Play, "rock clásico")]
    [InlineData("busca baladas románticas", VoiceAction.Play, "baladas románticas")]
    [InlineData("escucha cumbia", VoiceAction.Play, "cumbia")]
    [InlineData("ponme bachata", VoiceAction.Play, "bachata")]
    [InlineData("quiero escuchar merengue", VoiceAction.Play, "merengue")]
    [InlineData("toca vallenato", VoiceAction.Play, "vallenato")]
    public void PlayCommands_ExtractQuery(string input, VoiceAction expectedAction, string expectedQuery)
    {
        var cmd = VoiceCommandParser.Parse(input);
        Assert.Equal(expectedAction, cmd.Action);
        Assert.Contains(expectedQuery, cmd.Query, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════
    //  ARTIST EXTRACTION
    // ═══════════════════════════════════════════

    [Fact]
    public void PlayWithArtist_ExtractsArtistFromDe()
    {
        var cmd = VoiceCommandParser.Parse("reproduce música de Bon Jovi");
        Assert.Equal(VoiceAction.Play, cmd.Action);
        Assert.NotNull(cmd.Artist);
        Assert.Contains("Bon Jovi", cmd.Artist!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlayWithArtist_ExtractsFromQuery()
    {
        var cmd = VoiceCommandParser.Parse("pon canciones de Bad Bunny");
        Assert.Equal(VoiceAction.Play, cmd.Action);
        Assert.NotNull(cmd.Artist);
        Assert.Contains("Bad Bunny", cmd.Artist!, StringComparison.OrdinalIgnoreCase);
    }

    // ═══════════════════════════════════════════
    //  TARGET APP EXTRACTION
    // ═══════════════════════════════════════════

    [Fact]
    public void PlayInApp_ExtractsTargetApp()
    {
        var cmd = VoiceCommandParser.Parse("pon salsa en spotify");
        Assert.Equal(VoiceAction.Play, cmd.Action);
        Assert.NotNull(cmd.TargetApp);
        Assert.Contains("spotify", cmd.TargetApp!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PlayInApp_NoApp_TargetAppIsNull()
    {
        var cmd = VoiceCommandParser.Parse("reproduce reggaeton");
        Assert.Null(cmd.TargetApp);
    }

    // ═══════════════════════════════════════════
    //  FALLBACK: raw text as search query
    // ═══════════════════════════════════════════

    [Fact]
    public void Fallback_UnknownText_TreatedAsPlay()
    {
        var cmd = VoiceCommandParser.Parse("Shakira Waka Waka");
        Assert.Equal(VoiceAction.Play, cmd.Action);
        Assert.Contains("Shakira", cmd.Query);
    }

    [Fact]
    public void EmptyInput_ReturnsUnknown()
    {
        var cmd = VoiceCommandParser.Parse("");
        Assert.Equal(VoiceAction.Unknown, cmd.Action);
    }

    [Fact]
    public void WhitespaceInput_ReturnsUnknown()
    {
        var cmd = VoiceCommandParser.Parse("   ");
        Assert.Equal(VoiceAction.Unknown, cmd.Action);
    }

    // ═══════════════════════════════════════════
    //  FULL VOICE SESSION: wake word → parse → action
    // ═══════════════════════════════════════════

    [Fact]
    public void FullSession_WakeWordThenPlay()
    {
        var text = "gemini reproduce salsa romántica";
        var afterWake = VoiceCommandParser.ExtractAfterWakeWord(text);
        Assert.NotNull(afterWake);

        var cmd = VoiceCommandParser.Parse(afterWake!);
        Assert.Equal(VoiceAction.Play, cmd.Action);
        Assert.Contains("salsa", cmd.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FullSession_WakeWordThenStop()
    {
        var text = "asistente para la música";
        var afterWake = VoiceCommandParser.ExtractAfterWakeWord(text);
        Assert.NotNull(afterWake);

        var cmd = VoiceCommandParser.Parse(afterWake!);
        Assert.Equal(VoiceAction.Stop, cmd.Action);
    }

    [Fact]
    public void FullSession_WakeWordThenNext()
    {
        var text = "hey music siguiente canción";
        var afterWake = VoiceCommandParser.ExtractAfterWakeWord(text);
        Assert.NotNull(afterWake);

        var cmd = VoiceCommandParser.Parse(afterWake!);
        Assert.Equal(VoiceAction.Next, cmd.Action);
    }

    [Fact]
    public void FullSession_WakeWordThenArtistSearch()
    {
        var text = "gemini pon música de Marc Anthony";
        var afterWake = VoiceCommandParser.ExtractAfterWakeWord(text);
        Assert.NotNull(afterWake);

        var cmd = VoiceCommandParser.Parse(afterWake!);
        Assert.Equal(VoiceAction.Play, cmd.Action);
        Assert.NotNull(cmd.Artist);
        Assert.Contains("Marc Anthony", cmd.Artist!, StringComparison.OrdinalIgnoreCase);
    }
}
