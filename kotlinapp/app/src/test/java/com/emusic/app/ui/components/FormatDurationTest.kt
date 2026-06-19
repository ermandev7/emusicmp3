package com.emusic.app.ui.components

import com.google.common.truth.Truth.assertThat
import org.junit.Test

class FormatDurationTest {

    @Test
    fun `cero segundos`() {
        assertThat(formatDuration(0)).isEqualTo("0:00")
    }

    @Test
    fun `segundos con cero a la izquierda`() {
        assertThat(formatDuration(5)).isEqualTo("0:05")
    }

    @Test
    fun `un minuto y cinco segundos`() {
        assertThat(formatDuration(65)).isEqualTo("1:05")
    }

    @Test
    fun `cancion tipica de tres minutos y medio`() {
        assertThat(formatDuration(210)).isEqualTo("3:30")
    }

    @Test
    fun `mas de diez minutos`() {
        assertThat(formatDuration(754)).isEqualTo("12:34")
    }
}
