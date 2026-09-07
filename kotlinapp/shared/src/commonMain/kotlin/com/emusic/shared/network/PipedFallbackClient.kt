package com.emusic.shared.network

import com.emusic.shared.api.SearchResponse
import com.emusic.shared.api.StreamInfo
import io.ktor.client.HttpClient
import io.ktor.client.call.body
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.client.request.get
import io.ktor.client.request.parameter
import io.ktor.serialization.kotlinx.json.json
import kotlinx.serialization.json.Json

/**
 * Fase 3: espejo de com.emusic.app.data.api.FallbackApiService/FallbackPipedApi.
 * Cuando el backend propio (MusicApiClient) falla, se prueban estas instancias
 * públicas de Piped en orden hasta obtener una respuesta con contenido. Misma
 * lista y mismo orden que usa hoy la app Android (NetworkModule/MusicRepository)
 * — no se agrega ni quita ninguna, para no cambiar el comportamiento existente.
 */
private val FALLBACK_BASES = listOf(
    "https://pipedapi.kavin.rocks",
    "https://pipedapi.colby.land",
    "https://piped-api.garudalinux.org",
    "https://api.piped.yt"
)

/**
 * Cliente HTTP separado para los fallbacks: timeouts cortos (7s/7s), igual que el
 * OkHttpClient dedicado que arma NetworkModule.provideFallbackApiService, para no
 * bloquear la reproducción esperando una instancia pública caída.
 */
fun createPipedFallbackHttpClient(): HttpClient = HttpClient {
    install(ContentNegotiation) {
        json(Json {
            ignoreUnknownKeys = true
            isLenient = true
            coerceInputValues = true
        })
    }
    install(HttpTimeout) {
        connectTimeoutMillis = 7_000
        requestTimeoutMillis = 7_000
    }
    expectSuccess = true
}

/**
 * Prueba cada instancia pública de Piped en orden y devuelve la primera respuesta
 * con contenido. Nunca lanza: ante fallo total devuelve null, igual que
 * MusicRepository.search()/getStream() devuelven emptyList()/null hoy.
 */
class PipedFallbackClient(private val httpClient: HttpClient) {

    suspend fun search(query: String, filter: String = "music_songs"): SearchResponse? {
        for (base in FALLBACK_BASES) {
            try {
                val res: SearchResponse = httpClient.get("$base/search") {
                    parameter("q", query)
                    parameter("filter", filter)
                }.body()
                if (res.items.isNotEmpty()) return res
            } catch (e: Exception) {
                // se sigue con la próxima instancia
            }
        }
        return null
    }

    suspend fun getStream(videoId: String): StreamInfo? {
        for (base in FALLBACK_BASES) {
            try {
                val res: StreamInfo = httpClient.get("$base/streams/$videoId").body()
                if (res.audioStreams.isNotEmpty()) return res
            } catch (e: Exception) {
                // se sigue con la próxima instancia
            }
        }
        return null
    }
}
