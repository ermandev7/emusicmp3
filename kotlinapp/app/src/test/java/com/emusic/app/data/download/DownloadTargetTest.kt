package com.emusic.app.data.download

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * Tests de la decisión de descarga. El bug que rompía la descarga era guardar
 * audio/webm en MediaStore.Audio (lo rechaza). Estos tests fijan el contrato:
 * webm/opus → carpeta Descargas; formatos compatibles → carpeta Música.
 */
class DownloadTargetTest {

    @Test
    fun `webm va a Descargas y no a Musica`() {
        val t = DownloadTarget.forStreamMime("audio/webm")
        assertThat(t.extension).isEqualTo("webm")
        assertThat(t.mimeType).isEqualTo("audio/webm")
        assertThat(t.toMusicFolder).isFalse()
    }

    @Test
    fun `m4a va a Musica`() {
        val t = DownloadTarget.forStreamMime("audio/mp4")
        assertThat(t.extension).isEqualTo("m4a")
        assertThat(t.mimeType).isEqualTo("audio/mp4")
        assertThat(t.toMusicFolder).isTrue()
    }

    @Test
    fun `mpeg mp3 va a Musica`() {
        val t = DownloadTarget.forStreamMime("audio/mpeg")
        assertThat(t.extension).isEqualTo("mp3")
        assertThat(t.toMusicFolder).isTrue()
    }

    @Test
    fun `opus se mapea a ogg y va a Musica`() {
        val t = DownloadTarget.forStreamMime("audio/opus")
        assertThat(t.extension).isEqualTo("ogg")
        assertThat(t.mimeType).isEqualTo("audio/ogg")
        assertThat(t.toMusicFolder).isTrue()
    }

    @Test
    fun `mime desconocido cae a m4a compatible`() {
        val t = DownloadTarget.forStreamMime("audio/x-rara")
        assertThat(t.extension).isEqualTo("m4a")
        assertThat(t.toMusicFolder).isTrue()
    }
}
