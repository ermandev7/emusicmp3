package com.emusic.app.data.api

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * Tests de la selección de stream de audio. Esta lógica decide qué URL se
 * reproduce (también en el comando de voz de Android Auto) y qué se descarga.
 */
class StreamSelectionTest {

    private fun audio(bitrate: Int, mime: String = "audio/webm", url: String = "u$bitrate") =
        AudioStream(url = url, bitrate = bitrate, mimeType = mime)

    @Test
    fun `elige el de mayor bitrate`() {
        val info = StreamInfo(audioStreams = listOf(audio(128_000), audio(256_000), audio(64_000)))
        assertThat(info.bestAudioStream()?.bitrate).isEqualTo(256_000)
    }

    @Test
    fun `ignora streams que no son audio`() {
        val info = StreamInfo(
            audioStreams = listOf(
                audio(320_000, mime = "video/mp4"),
                audio(128_000, mime = "audio/mp4")
            )
        )
        assertThat(info.bestAudioStream()?.mimeType).isEqualTo("audio/mp4")
    }

    @Test
    fun `sin streams devuelve null`() {
        assertThat(StreamInfo(audioStreams = emptyList()).bestAudioStream()).isNull()
    }

    @Test
    fun `bestAudioUrl devuelve la url del mejor`() {
        val info = StreamInfo(audioStreams = listOf(audio(128_000, url = "low"), audio(256_000, url = "high")))
        assertThat(info.bestAudioUrl()).isEqualTo("high")
    }

    @Test
    fun `bestAudioUrl null cuando la url esta vacia`() {
        val info = StreamInfo(audioStreams = listOf(audio(256_000, url = "")))
        assertThat(info.bestAudioUrl()).isNull()
    }
}
