package com.emusic.app.data.api

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * Tests del modelo [Track], en especial la extracción de videoId desde
 * distintos formatos de URL (un bug recurrente al integrar Piped/Invidious).
 */
class TrackTest {

    @Test
    fun `videoId usa el campo JSON si esta presente`() {
        val t = Track(url = "/watch?v=OTHER", videoIdFromJson = "ABC123")
        assertThat(t.videoId).isEqualTo("ABC123")
    }

    @Test
    fun `videoId se extrae de url watch`() {
        val t = Track(url = "/watch?v=dQw4w9WgXcQ")
        assertThat(t.videoId).isEqualTo("dQw4w9WgXcQ")
    }

    @Test
    fun `videoId ignora parametros extra como list`() {
        val t = Track(url = "/watch?v=dQw4w9WgXcQ&list=PL123&index=2")
        assertThat(t.videoId).isEqualTo("dQw4w9WgXcQ")
    }

    @Test
    fun `videoId con guiones bajos y guiones se conserva`() {
        val t = Track(url = "/watch?v=a_b-C123")
        assertThat(t.videoId).isEqualTo("a_b-C123")
    }

    @Test
    fun `videoId desde url sin watch quita slash inicial`() {
        val t = Track(url = "/ABC123")
        assertThat(t.videoId).isEqualTo("ABC123")
    }

    @Test
    fun `displayArtist prioriza uploaderName`() {
        val t = Track(uploaderName = "Uploader", uploader = "Otro", artist = "X")
        assertThat(t.displayArtist).isEqualTo("Uploader")
    }

    @Test
    fun `displayArtist cae a author cuando faltan los demas`() {
        val t = Track(author = "Autor")
        assertThat(t.displayArtist).isEqualTo("Autor")
    }

    @Test
    fun `displayArtist vacio cuando no hay datos`() {
        assertThat(Track().displayArtist).isEqualTo("")
    }

    @Test
    fun `displayThumbnail prioriza thumbnail sobre thumbnailUrl`() {
        val t = Track(thumbnail = "a.jpg", thumbnailUrl = "b.jpg")
        assertThat(t.displayThumbnail).isEqualTo("a.jpg")
    }

    @Test
    fun `hqThumbnail mejora calidad de mqdefault`() {
        val t = Track(thumbnail = "https://i.ytimg.com/vi/ID/mqdefault.jpg")
        assertThat(t.hqThumbnail).contains("maxresdefault")
    }

    @Test
    fun `hqThumbnail usa videoId cuando no hay thumbnail`() {
        val t = Track(url = "/watch?v=ID123")
        assertThat(t.hqThumbnail).contains("ID123")
        assertThat(t.hqThumbnail).contains("maxresdefault")
    }
}
