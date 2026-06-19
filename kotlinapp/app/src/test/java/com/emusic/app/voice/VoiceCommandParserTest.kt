package com.emusic.app.voice

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * Tests del parser de comandos de voz. Cubre los 8 comandos reales de uso
 * (pon X, billie jean de X, stop, siguiente, busca X, anterior, pon X en emusic)
 * más casos límite que suelen romper este tipo de parsers.
 */
class VoiceCommandParserTest {

    // ─── Comandos de reproducción ────────────────────────────────────────

    @Test
    fun `pon musica de artista extrae el artista como query`() {
        val cmd = VoiceCommandParser.parse("pon música de bon jovi")
        assertThat(cmd.action).isEqualTo(VoiceAction.Play)
        assertThat(cmd.query).ignoringCase().contains("bon jovi")
    }

    @Test
    fun `pon cancion de artista detecta el artista`() {
        val cmd = VoiceCommandParser.parse("pon billie jean de michael jackson")
        assertThat(cmd.action).isEqualTo(VoiceAction.Play)
        assertThat(cmd.query).isNotEmpty()
        assertThat(cmd.artist).ignoringCase().contains("michael jackson")
    }

    @Test
    fun `busca genero devuelve query de busqueda`() {
        val cmd = VoiceCommandParser.parse("busca salsa")
        assertThat(cmd.action).isEqualTo(VoiceAction.Play)
        assertThat(cmd.query).isEqualTo("salsa")
    }

    @Test
    fun `pon X en emusic detecta target app`() {
        val cmd = VoiceCommandParser.parse("pon rock en emusic")
        assertThat(cmd.action).isEqualTo(VoiceAction.Play)
        assertThat(cmd.query).isEqualTo("rock")
        assertThat(cmd.targetApp).ignoringCase().contains("emusic")
    }

    @Test
    fun `reproduce con palabra para no se confunde con stop`() {
        // "para" aparece dentro de la frase pero es un comando de reproducción.
        val cmd = VoiceCommandParser.parse("reproduce una canción para relajarme")
        assertThat(cmd.action).isEqualTo(VoiceAction.Play)
    }

    // ─── Comandos de control ─────────────────────────────────────────────

    @Test
    fun `stop con texto extra se interpreta como stop`() {
        val cmd = VoiceCommandParser.parse("stop la musica")
        assertThat(cmd.action).isEqualTo(VoiceAction.Stop)
        assertThat(cmd.query).isEmpty()
    }

    @Test
    fun `stop solo`() {
        assertThat(VoiceCommandParser.parse("stop").action).isEqualTo(VoiceAction.Stop)
    }

    @Test
    fun `siguiente cancion es next`() {
        assertThat(VoiceCommandParser.parse("siguiente canción").action).isEqualTo(VoiceAction.Next)
    }

    @Test
    fun `anterior cancion es previous`() {
        assertThat(VoiceCommandParser.parse("anterior canción").action).isEqualTo(VoiceAction.Previous)
    }

    @Test
    fun `pausa es pause`() {
        assertThat(VoiceCommandParser.parse("pausa").action).isEqualTo(VoiceAction.Pause)
    }

    @Test
    fun `continua es resume`() {
        assertThat(VoiceCommandParser.parse("continúa").action).isEqualTo(VoiceAction.Resume)
    }

    // ─── Casos límite ────────────────────────────────────────────────────

    @Test
    fun `texto vacio es unknown`() {
        assertThat(VoiceCommandParser.parse("").action).isEqualTo(VoiceAction.Unknown)
    }

    @Test
    fun `solo espacios es unknown`() {
        assertThat(VoiceCommandParser.parse("    ").action).isEqualTo(VoiceAction.Unknown)
    }

    // ─── Micrófono de la app: parseTransport (transporte vs búsqueda) ────

    @Test
    fun `parseTransport siguiente cancion es Next`() {
        assertThat(VoiceCommandParser.parseTransport("siguiente canción")).isEqualTo(VoiceAction.Next)
    }

    @Test
    fun `parseTransport stop cancion es Stop`() {
        assertThat(VoiceCommandParser.parseTransport("stop canción")).isEqualTo(VoiceAction.Stop)
    }

    @Test
    fun `parseTransport anterior cancion es Previous`() {
        assertThat(VoiceCommandParser.parseTransport("anterior canción")).isEqualTo(VoiceAction.Previous)
    }

    @Test
    fun `parseTransport pausa la musica es Pause`() {
        assertThat(VoiceCommandParser.parseTransport("pausa la música")).isEqualTo(VoiceAction.Pause)
    }

    @Test
    fun `parseTransport continua es Resume`() {
        assertThat(VoiceCommandParser.parseTransport("continúa")).isEqualTo(VoiceAction.Resume)
    }

    @Test
    fun `parseTransport no confunde titulo que empieza con palabra de control`() {
        // "para siempre" y "sigue bailando" son canciones, no comandos.
        assertThat(VoiceCommandParser.parseTransport("para siempre")).isNull()
        assertThat(VoiceCommandParser.parseTransport("sigue bailando")).isNull()
    }

    @Test
    fun `parseTransport de una busqueda normal devuelve null`() {
        assertThat(VoiceCommandParser.parseTransport("bon jovi")).isNull()
        assertThat(VoiceCommandParser.parseTransport("reproduce bon jovi")).isNull()
    }

    // ─── Micrófono de la app: parseSearch (quita el verbo) ───────────────

    @Test
    fun `parseSearch quita el verbo reproduce`() {
        val cmd = VoiceCommandParser.parseSearch("reproduce bon jovi")
        assertThat(cmd.action).isEqualTo(VoiceAction.Play)
        assertThat(cmd.query).ignoringCase().isEqualTo("bon jovi")
    }

    @Test
    fun `parseSearch sin verbo usa el texto completo`() {
        val cmd = VoiceCommandParser.parseSearch("bad bunny")
        assertThat(cmd.action).isEqualTo(VoiceAction.Play)
        assertThat(cmd.query).ignoringCase().isEqualTo("bad bunny")
    }

    // ─── Wake words ──────────────────────────────────────────────────────

    @Test
    fun `extractAfterWakeWord devuelve el texto tras gemini`() {
        val after = VoiceCommandParser.extractAfterWakeWord("gemini pon música de bon jovi")
        assertThat(after).isEqualTo("pon música de bon jovi")
    }

    @Test
    fun `extractAfterWakeWord sin wake word devuelve null`() {
        assertThat(VoiceCommandParser.extractAfterWakeWord("hola mundo")).isNull()
    }

    @Test
    fun `containsWakeWord detecta asistente`() {
        assertThat(VoiceCommandParser.containsWakeWord("asistente siguiente")).isTrue()
    }

    @Test
    fun `flujo completo wake word mas comando`() {
        val after = VoiceCommandParser.extractAfterWakeWord("gemini siguiente canción")
        assertThat(after).isNotNull()
        val cmd = VoiceCommandParser.parse(after!!)
        assertThat(cmd.action).isEqualTo(VoiceAction.Next)
    }
}
