package com.emusic.app.player

import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * Tests de [SkipFilter]: distinguir "no me gusta esta canción" de "estoy pasando
 * canciones buscando otra". Un falso skip penaliza a un artista que al usuario le
 * gusta, así que ante la duda se prefiere no marcar.
 */
class SkipFilterTest {

    private var clock = 0L
    private val filter = SkipFilter<String>(burstGapMs = 12_000) { clock }

    @Test
    fun `un siguiente aislado queda pendiente y se confirma`() {
        clock = 100_000
        filter.onUserTrackChange("A")
        assertThat(filter.pending).isEqualTo("A")
        assertThat(filter.confirm()).isEqualTo("A")
        assertThat(filter.pending).isNull()
    }

    @Test
    fun `buscar canción pulsando siguiente varias veces no marca ningún skip`() {
        // Suena A (le gusta) a los 10 s → siguiente; B y C suenan un par de
        // segundos cada una → siguiente; se queda en D.
        clock = 100_000; filter.onUserTrackChange("A")
        clock = 106_000; filter.onUserTrackChange("B")
        clock = 111_000; filter.onUserTrackChange("C")
        assertThat(filter.confirm()).isNull()
    }

    @Test
    fun `la canción de la que partió la búsqueda tampoco se marca`() {
        clock = 100_000; filter.onUserTrackChange("A")
        // Antes de que la siguiente suene lo bastante, otro cambio: era búsqueda.
        clock = 130_000; val discarded = filter.onUserTrackChange("B")
        assertThat(discarded).isTrue()
        assertThat(filter.confirm()).isNull()
    }

    @Test
    fun `búsqueda que empieza en una canción ya escuchada tampoco marca`() {
        // A llevaba más de 30 s (no es candidata), B se salta en seguida: dos cambios
        // juntos = búsqueda, B no cuenta.
        clock = 100_000; filter.onUserTrackChange(null)
        clock = 105_000; filter.onUserTrackChange("B")
        assertThat(filter.confirm()).isNull()
    }

    @Test
    fun `anterior cancela el skip pendiente porque vuelve a esa canción`() {
        clock = 100_000; filter.onUserTrackChange("A")
        clock = 103_000; filter.onUserTrackChange(null) // anterior
        assertThat(filter.confirm()).isNull()
    }

    @Test
    fun `dos skips separados en el tiempo cuentan los dos`() {
        clock = 100_000; filter.onUserTrackChange("A")
        assertThat(filter.confirm()).isEqualTo("A")
        clock = 300_000; filter.onUserTrackChange("B")
        assertThat(filter.confirm()).isEqualTo("B")
    }

    @Test
    fun `tras una búsqueda, un siguiente posterior y aislado vuelve a contar`() {
        clock = 100_000; filter.onUserTrackChange("A")
        clock = 105_000; filter.onUserTrackChange("B")
        assertThat(filter.confirm()).isNull()
        clock = 400_000; filter.onUserTrackChange("X")
        assertThat(filter.confirm()).isEqualTo("X")
    }
}
