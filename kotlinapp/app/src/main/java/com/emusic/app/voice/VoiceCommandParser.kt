package com.emusic.app.voice

enum class VoiceAction {
    Play, Stop, Pause, Resume, Next, Previous, Unknown
}

data class VoiceCommand(
    val action: VoiceAction,
    val query: String,
    val artist: String?,
    val targetApp: String?
)

object VoiceCommandParser {

    private val wakeWords = listOf(
        "asistente", "gemini", "oye música", "oye musica",
        "hey music", "ok music", "hey asistente"
    )

    // Límites de palabra (\b) para no matchear dentro de otras palabras
    // (ej. "para" en "disparar"). Se evalúan DESPUÉS de los verbos de reproducción.
    private val controlPatterns = listOf(
        Regex("""\b(para|detén|deten|stop|parar)\b""", RegexOption.IGNORE_CASE) to VoiceAction.Stop,
        Regex("""\b(pausa|pausar|pause)\b""", RegexOption.IGNORE_CASE) to VoiceAction.Pause,
        Regex("""\b(continúa|continua|resume|reanudar|reanuda|sigue)\b""", RegexOption.IGNORE_CASE) to VoiceAction.Resume,
        Regex("""\b(siguiente|next|skip|salta)\b""", RegexOption.IGNORE_CASE) to VoiceAction.Next,
        Regex("""\b(anterior|previous|atrás|atras)\b""", RegexOption.IGNORE_CASE) to VoiceAction.Previous,
    )

    private val playPattern = Regex(
        """(?:pon|reproduce|reproducir|play|tocar|toca|busca|buscar|escuchar|escucha|quiero(?:\s+escuchar)?|ponme|pon\s*me)\s+(.+)""",
        RegexOption.IGNORE_CASE
    )
    private val artistPattern = Regex("""(?:de|by)\s+(.+?)(?:\s+en\s+.+)?$""", RegexOption.IGNORE_CASE)
    private val targetAppPattern = Regex("""\s+en\s+([\w\s]+)$""", RegexOption.IGNORE_CASE)
    private val musicPrefixPattern = Regex("""^(?:música|musica|canciones?|temas?|algo)\s+(?:de|del)\s+""", RegexOption.IGNORE_CASE)

    fun extractAfterWakeWord(text: String): String? {
        val lower = text.lowercase().trim()
        for (ww in wakeWords) {
            val idx = lower.indexOf(ww)
            if (idx >= 0) {
                val after = text.substring(idx + ww.length).trim()
                return if (after.isNotEmpty()) after else ""
            }
        }
        return null
    }

    fun containsWakeWord(text: String): Boolean = extractAfterWakeWord(text) != null

    // Palabras de relleno que se ignoran al detectar un comando de transporte
    // ("siguiente canción", "pausa la música" → "siguiente", "pausa").
    private val transportFiller = Regex(
        """\b(la|el|las|los|una?|esta|este|esa|ese|mi|de|del|música|musica|canción|cancion|canciones|tema|temas|por|favor|porfavor|actual|ahora|esto|que|está|esta|reproduciendo|sonando|pista|este)\b""",
        RegexOption.IGNORE_CASE
    )

    /**
     * Para el micrófono de la app (modo búsqueda directa): detecta si la frase es
     * claramente un comando de transporte (siguiente, pausa, anterior…) para poder
     * controlar la reproducción por voz. Devuelve null cuando debe tratarse como
     * búsqueda. Robusto: quita palabras de relleno y exige que lo que queda sea
     * EXACTAMENTE una palabra de control, así un título como "sigue bailando" o
     * "para siempre" se sigue tratando como búsqueda.
     */
    fun parseTransport(text: String): VoiceAction? {
        val cleaned = text.lowercase().trim()
            .replace(transportFiller, " ")
            .replace(Regex("\\s+"), " ")
            .trim()
        if (cleaned.isEmpty()) return null
        return when {
            Regex("^(siguiente|próxima|proxima|próximo|proximo|next|skip|salta|sáltala|saltala|pasa|pásala|pasala|adelante|avanza)$").matches(cleaned) -> VoiceAction.Next
            Regex("^(anterior|previa|previo|previous|atrás|atras|regresa|retrocede|vuelve|devuélvete|devuelvete)$").matches(cleaned) -> VoiceAction.Previous
            Regex("^(pausa|pausar|pause|páusala|pausala)$").matches(cleaned) -> VoiceAction.Pause
            Regex("^(para|pará|alto|detén|deten|detente|detener|stop|parar)$").matches(cleaned) -> VoiceAction.Stop
            Regex("^(continúa|continua|continuar|resume|reanuda|reanudar|sigue|seguir)$").matches(cleaned) -> VoiceAction.Resume
            else -> null
        }
    }

    /**
     * Extrae la consulta de búsqueda quitando el verbo de reproducción
     * ("reproduce bon jovi" → "bon jovi"). Nunca devuelve un comando de control;
     * úsalo para el caso de búsqueda una vez descartado el transporte.
     */
    fun parseSearch(text: String): VoiceCommand {
        val trimmed = text.trim()
        val playMatch = playPattern.find(trimmed)
        return if (playMatch != null) extractPlayCommand(playMatch.groupValues[1].trim())
               else extractPlayCommand(trimmed)
    }

    fun parse(text: String): VoiceCommand {
        if (text.isBlank()) return VoiceCommand(VoiceAction.Unknown, "", null, null)
        val trimmed = text.trim()

        // 1. Un verbo de reproducción explícito gana sobre los comandos de control.
        //    Así "reproduce algo para relajarme" no se confunde con "parar".
        val playMatch = playPattern.find(trimmed)
        if (playMatch != null) return extractPlayCommand(playMatch.groupValues[1].trim())

        // 2. Comandos de control (stop / pausa / siguiente / anterior / resume).
        for ((pattern, action) in controlPatterns) {
            if (pattern.containsMatchIn(trimmed))
                return VoiceCommand(action, "", null, null)
        }

        // 3. Sin verbo ni comando: tratar todo como búsqueda.
        return extractPlayCommand(trimmed)
    }

    private fun extractPlayCommand(raw: String): VoiceCommand {
        var workRaw = raw
        var targetApp: String? = null
        var artist: String? = null

        val appMatch = targetAppPattern.find(workRaw)
        if (appMatch != null) {
            targetApp = appMatch.groupValues[1].trim()
            workRaw = workRaw.substring(0, appMatch.range.first).trim()
        }

        val artistMatch = artistPattern.find(workRaw)
        if (artistMatch != null) artist = artistMatch.groupValues[1].trim()

        var query = workRaw
        val prefixMatch = musicPrefixPattern.find(query)
        if (prefixMatch != null && artist != null) {
            query = artist
        }

        if (artist == null) {
            val deIdx = query.lastIndexOf(" de ", ignoreCase = true)
            if (deIdx >= 0) artist = query.substring(deIdx + 4).trim()
        }

        return VoiceCommand(VoiceAction.Play, query, artist, targetApp)
    }
}
