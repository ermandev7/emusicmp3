package com.emusic.shared.voice

import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertNull
import kotlin.test.assertTrue

/**
 * Smoke test multiplataforma del parser de voz (corre en Android y en el
 * simulador/dispositivo iOS). La suite completa y detallada sigue viviendo en
 * app/src/test (JVM/JUnit/Truth) hasta que la Fase 4 mueva la app Android a
 * consumir este módulo compartido — no se duplica caso por caso, solo se
 * valida acá que la lógica funcione igual en ambas plataformas.
 */
class VoiceCommandParserTest {

    @Test
    fun `pon musica de artista extrae el artista como query`() {
        val cmd = VoiceCommandParser.parse("pon música de bon jovi")
        assertEquals(VoiceAction.Play, cmd.action)
        assertTrue(cmd.query.contains("bon jovi", ignoreCase = true))
    }

    @Test
    fun `busca genero devuelve query de busqueda`() {
        val cmd = VoiceCommandParser.parse("busca salsa")
        assertEquals(VoiceAction.Play, cmd.action)
        assertEquals("salsa", cmd.query)
    }

    @Test
    fun `comando de control siguiente`() {
        val cmd = VoiceCommandParser.parse("siguiente")
        assertEquals(VoiceAction.Next, cmd.action)
    }

    @Test
    fun `comando de control pausa`() {
        val cmd = VoiceCommandParser.parse("pausa")
        assertEquals(VoiceAction.Pause, cmd.action)
    }

    @Test
    fun `titulo que contiene una palabra de control se trata como busqueda`() {
        // "para siempre" no debe interpretarse como el comando "parar".
        val cmd = VoiceCommandParser.parse("pon para siempre")
        assertEquals(VoiceAction.Play, cmd.action)
        assertTrue(cmd.query.contains("para siempre", ignoreCase = true))
    }

    @Test
    fun `parseTransport reconoce siguiente sin verbo`() {
        assertEquals(VoiceAction.Next, VoiceCommandParser.parseTransport("siguiente canción"))
    }

    @Test
    fun `parseTransport no confunde un titulo con un comando`() {
        // Búsqueda directa (micrófono de la app): "sigue bailando" es un título, no "sigue".
        assertNull(VoiceCommandParser.parseTransport("sigue bailando"))
    }

    @Test
    fun `extractAfterWakeWord detecta la wake word y devuelve el resto`() {
        val resto = VoiceCommandParser.extractAfterWakeWord("oye música pon bon jovi")
        assertEquals("pon bon jovi", resto)
    }

    @Test
    fun `extractAfterWakeWord devuelve null sin wake word`() {
        assertNull(VoiceCommandParser.extractAfterWakeWord("pon bon jovi"))
    }
}
