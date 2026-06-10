using System.Text.RegularExpressions;

namespace eMusicApp.Services;

/// <summary>
/// Parsea texto de voz en español → VoiceCommand.
/// Soporta: "pon música de Bon Jovi", "reproduce salsa", "siguiente", "para la música", etc.
/// </summary>
public static class VoiceCommandParser
{
    // ── Wake words ──
    private static readonly string[] WakeWords =
    {
        "asistente", "gemini", "oye música", "hey music", "ok music", "hey asistente"
    };

    /// <summary>
    /// Detecta si el texto contiene un wake word.
    /// Retorna el texto DESPUÉS del wake word, o null si no se encontró.
    /// </summary>
    public static string? ExtractAfterWakeWord(string text)
    {
        var lower = text.ToLowerInvariant().Trim();
        foreach (var ww in WakeWords)
        {
            var idx = lower.IndexOf(ww, StringComparison.Ordinal);
            if (idx >= 0)
            {
                var after = text.Substring(idx + ww.Length).Trim();
                return after.Length > 0 ? after : "";
            }
        }
        return null;
    }

    /// <summary>Detecta si el texto contiene wake word (sin extraer).</summary>
    public static bool ContainsWakeWord(string text)
        => ExtractAfterWakeWord(text) != null;

    // ── Patrones de comandos ──

    // Comandos de control (sin query)
    private static readonly (Regex pattern, VoiceAction action)[] ControlPatterns =
    {
        (new Regex(@"\b(para|detén|deten|stop|parar)\b", RegexOptions.IgnoreCase), VoiceAction.Stop),
        (new Regex(@"\b(pausa|pausar|pause)\b", RegexOptions.IgnoreCase), VoiceAction.Pause),
        (new Regex(@"\b(continúa|continua|resume|reanudar|reanuda|sigue)\b", RegexOptions.IgnoreCase), VoiceAction.Resume),
        (new Regex(@"\b(siguiente|next|skip|salta)\b", RegexOptions.IgnoreCase), VoiceAction.Next),
        (new Regex(@"\b(anterior|previous|atrás|atras)\b", RegexOptions.IgnoreCase), VoiceAction.Previous),
    };

    // Patrones de reproducción: "pon/reproduce/busca [query] (de [artista]) (en [app])"
    private static readonly Regex PlayPattern = new(
        @"(?:pon|reproduce|reproducir|play|tocar|toca|busca|buscar|escuchar|escucha|quiero(?:\s+escuchar)?|ponme|pon\s*me)\s+(.+)",
        RegexOptions.IgnoreCase);

    // Extraer "de [artista]" al final: "música de Bon Jovi" → artista = "Bon Jovi"
    private static readonly Regex ArtistPattern = new(
        @"\b(?:de|by)\s+(.+?)(?:\s+en\s+.+)?$",
        RegexOptions.IgnoreCase);

    // Extraer "en [app]" al final: "salsa en emusicapp" → app = "emusicapp"
    private static readonly Regex TargetAppPattern = new(
        @"\s+en\s+([\w\s]+)$",
        RegexOptions.IgnoreCase);

    // Limpiar prefijos genéricos: "música de", "canciones de", "algo de"
    private static readonly Regex MusicPrefixPattern = new(
        @"^(?:música|musica|canciones?|temas?|algo)\s+(?:de|del)\s+",
        RegexOptions.IgnoreCase);

    /// <summary>
    /// Parsea un comando de voz (ya sin wake word) en un VoiceCommand estructurado.
    /// </summary>
    public static VoiceCommand Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new VoiceCommand(VoiceAction.Unknown, "", null, null);

        text = text.Trim();

        // 1. Comandos de control simples
        foreach (var (pattern, action) in ControlPatterns)
        {
            if (pattern.IsMatch(text))
                return new VoiceCommand(action, "", null, null);
        }

        // 2. Comandos de reproducción/búsqueda
        var playMatch = PlayPattern.Match(text);
        if (playMatch.Success)
        {
            var raw = playMatch.Groups[1].Value.Trim();
            return ExtractPlayCommand(raw);
        }

        // 3. Fallback: tratar todo el texto como búsqueda de reproducción
        return ExtractPlayCommand(text);
    }

    private static VoiceCommand ExtractPlayCommand(string raw)
    {
        string? targetApp = null;
        string? artist = null;

        // Extraer app destino: "... en emusicapp"
        var appMatch = TargetAppPattern.Match(raw);
        if (appMatch.Success)
        {
            targetApp = appMatch.Groups[1].Value.Trim();
            raw = raw.Substring(0, appMatch.Index).Trim();
        }

        // Extraer artista: "música de Bon Jovi"
        var artistMatch = ArtistPattern.Match(raw);
        if (artistMatch.Success)
            artist = artistMatch.Groups[1].Value.Trim();

        // Limpiar prefijos genéricos para mejorar la query de búsqueda
        var query = raw;
        var prefixMatch = MusicPrefixPattern.Match(query);
        if (prefixMatch.Success && artist != null)
        {
            // Si detectamos artista, la query es el artista directo
            query = artist;
        }

        // Si no se detectó artista pero hay "de" en el query, extraerlo
        if (artist == null && query.Contains(" de ", StringComparison.OrdinalIgnoreCase))
        {
            var deIdx = query.LastIndexOf(" de ", StringComparison.OrdinalIgnoreCase);
            artist = query.Substring(deIdx + 4).Trim();
        }

        return new VoiceCommand(VoiceAction.Play, query, artist, targetApp);
    }
}
