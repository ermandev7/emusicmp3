package com.emusic.shared.radio

import com.emusic.shared.api.Track
import kotlinx.coroutines.test.runTest
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertFalse
import kotlin.test.assertTrue

private fun track(id: String, artist: String = "Joan Sebastian") =
    Track(title = "tema $id", uploaderName = artist, videoIdFromJson = id)

/**
 * Fuente falsa: registra con qué límites se pidieron recomendadas, para poder verificar
 * la escalada que esquiva la caché de 30 min del backend.
 */
private class FakeRadioSource(
    val artistResults: List<Track> = emptyList(),
    val recommendationsByLimit: (Int) -> List<Track> = { emptyList() }
) : RadioSource {
    val requestedLimits = mutableListOf<Int>()
    var artistSearches = 0

    override suspend fun searchByArtist(artist: String): List<Track> {
        artistSearches++
        return artistResults
    }

    override suspend fun recommendations(limit: Int): List<Track> {
        requestedLimits += limit
        return recommendationsByLimit(limit)
    }
}

class RadioEngineTest {

    @Test
    fun noExtiendeSiEstaApagada() {
        val engine = RadioEngine(FakeRadioSource())
        assertFalse(engine.isActive)
        assertFalse(engine.shouldExtend(0))
    }

    @Test
    fun extiendeSoloCuandoQuedanPocasCanciones() {
        val engine = RadioEngine(FakeRadioSource())
        engine.start()
        assertTrue(engine.isActive)
        assertTrue(engine.shouldExtend(RadioEngine.LOW_QUEUE_THRESHOLD))
        assertTrue(engine.shouldExtend(1))
        assertFalse(engine.shouldExtend(RadioEngine.LOW_QUEUE_THRESHOLD + 1))
    }

    @Test
    fun stopApagaLaRadio() = runTest {
        val engine = RadioEngine(FakeRadioSource(artistResults = listOf(track("a"))))
        engine.start()
        engine.stop()
        assertFalse(engine.shouldExtend(0))
        assertEquals(emptyList(), engine.nextBatch("Joan Sebastian", emptySet()))
    }

    @Test
    fun priorizaElMismoArtistaYNoPideRecomendadas() = runTest {
        val source = FakeRadioSource(
            artistResults = listOf(track("a"), track("b")),
            recommendationsByLimit = { listOf(track("z", "Otro")) }
        )
        val engine = RadioEngine(source)
        engine.start()

        val batch = engine.nextBatch("Joan Sebastian", emptySet())

        assertEquals(setOf("a", "b"), batch.map { it.videoId }.toSet())
        assertTrue(source.requestedLimits.isEmpty(), "no debería pedir recomendadas si el artista alcanzó")
    }

    @Test
    fun caeARecomendadasCuandoElArtistaNoDaNadaNuevo() = runTest {
        val source = FakeRadioSource(
            artistResults = emptyList(),
            recommendationsByLimit = { listOf(track("r1", "Otro"), track("r2", "Otro")) }
        )
        val engine = RadioEngine(source)
        engine.start()

        val batch = engine.nextBatch("Joan Sebastian", emptySet())

        assertEquals(setOf("r1", "r2"), batch.map { it.videoId }.toSet())
        assertEquals(listOf(RadioEngine.BASE_LIMIT), source.requestedLimits)
    }

    @Test
    fun excluyeLoQueYaEstaEnLaCola() = runTest {
        val source = FakeRadioSource(artistResults = listOf(track("a"), track("b"), track("c")))
        val engine = RadioEngine(source)
        engine.start()

        val batch = engine.nextBatch("Joan Sebastian", existingIds = setOf("a", "c"))

        assertEquals(listOf("b"), batch.map { it.videoId })
    }

    @Test
    fun noRepiteCancionesEntreTandas() = runTest {
        val source = FakeRadioSource(artistResults = listOf(track("a"), track("b")))
        val engine = RadioEngine(source)
        engine.start()

        val primera = engine.nextBatch("Joan Sebastian", emptySet())
        val segunda = engine.nextBatch("Joan Sebastian", emptySet())

        assertEquals(setOf("a", "b"), primera.map { it.videoId }.toSet())
        assertTrue(segunda.isEmpty(), "la segunda tanda no debería repetir lo ya ofrecido")
    }

    @Test
    fun subeElLimiteCuandoLaCacheDevuelveSiempreLoMismo() = runTest {
        // El backend cachea 30 min: devuelve una página llena, toda ya conocida.
        val conocidas = (1..RadioEngine.BASE_LIMIT).map { track("viejo$it", "Otro") }
        val source = FakeRadioSource(
            recommendationsByLimit = { limit ->
                if (limit == RadioEngine.BASE_LIMIT) conocidas
                else conocidas + track("nuevo", "Otro")
            }
        )
        val engine = RadioEngine(source)
        engine.start()

        val batch = engine.nextBatch("", existingIds = conocidas.map { it.videoId }.toSet())

        assertEquals(listOf("nuevo"), batch.map { it.videoId })
        assertEquals(
            listOf(RadioEngine.BASE_LIMIT, RadioEngine.BASE_LIMIT + RadioEngine.LIMIT_STEP),
            source.requestedLimits
        )
    }

    @Test
    fun cortaLaEscaladaSiElAlgoritmoYaNoTieneMas() = runTest {
        // Menos resultados que el límite pedido = el pool se agotó, no tiene sentido seguir.
        val source = FakeRadioSource(
            recommendationsByLimit = { listOf(track("x", "Otro")) }
        )
        val engine = RadioEngine(source)
        engine.start()

        val batch = engine.nextBatch("", existingIds = setOf("x"))

        assertTrue(batch.isEmpty())
        assertEquals(listOf(RadioEngine.BASE_LIMIT), source.requestedLimits)
    }

    @Test
    fun limitaElTamanoDeLaTanda() = runTest {
        val muchas = (1..50).map { track("t$it") }
        val engine = RadioEngine(FakeRadioSource(artistResults = muchas))
        engine.start()

        val batch = engine.nextBatch("Joan Sebastian", emptySet())

        assertEquals(RadioEngine.BATCH_SIZE, batch.size)
    }
}
