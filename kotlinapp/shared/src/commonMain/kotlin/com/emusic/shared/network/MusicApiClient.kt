package com.emusic.shared.network

import com.emusic.shared.api.AddFavoriteRequest
import com.emusic.shared.api.AddHistoryRequest
import com.emusic.shared.api.AddToPlaylistRequest
import com.emusic.shared.api.CreatePlaylistRequest
import com.emusic.shared.api.ExcludeRequest
import com.emusic.shared.api.FavoriteEntry
import com.emusic.shared.api.GenreStat
import com.emusic.shared.api.HistoryEntry
import com.emusic.shared.api.Playlist
import com.emusic.shared.api.PrefetchRequest
import com.emusic.shared.api.RecommendationResponse
import com.emusic.shared.api.SearchResponse
import com.emusic.shared.api.StreamInfo
import io.ktor.client.HttpClient
import io.ktor.client.call.body
import io.ktor.client.request.delete
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.request.parameter
import io.ktor.client.request.patch
import io.ktor.client.request.post
import io.ktor.client.request.setBody
import io.ktor.http.ContentType
import io.ktor.http.contentType

/**
 * Fase 3: espejo de com.emusic.app.data.api.ApiService (Retrofit) usando Ktor, para
 * que Android e iOS compartan la misma capa de red. Cada método asume que el
 * [HttpClient] recibido tiene `expectSuccess = true` (ver [createEMusicHttpClient]):
 * una respuesta no-2xx lanza excepción, igual que Retrofit + el try/catch que ya
 * usa MusicRepository en el módulo `app`.
 *
 * @param userIdProvider cada plataforma decide dónde guarda el userId (DataStore en
 * Android, UserDefaults en iOS) — el cliente compartido no sabe ni le importa.
 */
class MusicApiClient(
    private val httpClient: HttpClient,
    private val baseUrl: String = "http://emusicmp3.duckdns.org:5050/api",
    private val userIdProvider: suspend () -> String
) {
    private suspend fun userIdHeader() = "X-User-Id" to userIdProvider()

    suspend fun search(query: String): SearchResponse {
        val (h, v) = userIdHeader()
        return httpClient.get("$baseUrl/search") {
            header(h, v)
            parameter("q", query)
        }.body()
    }

    suspend fun getStream(videoId: String): StreamInfo {
        val (h, v) = userIdHeader()
        return httpClient.get("$baseUrl/streams/$videoId") { header(h, v) }.body()
    }

    suspend fun prefetch(videoIds: List<String>) {
        val (h, v) = userIdHeader()
        httpClient.post("$baseUrl/streams/prefetch") {
            header(h, v)
            contentType(ContentType.Application.Json)
            setBody(PrefetchRequest(videoIds))
        }
    }

    suspend fun getTrending(): SearchResponse {
        val (h, v) = userIdHeader()
        return httpClient.get("$baseUrl/trending") { header(h, v) }.body()
    }

    suspend fun getFavorites(): List<FavoriteEntry> {
        val (h, v) = userIdHeader()
        return httpClient.get("$baseUrl/favorites") { header(h, v) }.body()
    }

    suspend fun addFavorite(request: AddFavoriteRequest) {
        val (h, v) = userIdHeader()
        httpClient.post("$baseUrl/favorites") {
            header(h, v)
            contentType(ContentType.Application.Json)
            setBody(request)
        }
    }

    suspend fun removeFavorite(videoId: String) {
        val (h, v) = userIdHeader()
        httpClient.delete("$baseUrl/favorites/$videoId") { header(h, v) }
    }

    /** true si existe (200 OK); false ante cualquier fallo (404 esperado si no es favorita). */
    suspend fun isFavorite(videoId: String): Boolean {
        val (h, v) = userIdHeader()
        return try {
            httpClient.get("$baseUrl/favorites/$videoId") { header(h, v) }
            true
        } catch (e: Exception) {
            false
        }
    }

    suspend fun getHistory(): List<HistoryEntry> {
        val (h, v) = userIdHeader()
        return httpClient.get("$baseUrl/history") { header(h, v) }.body()
    }

    suspend fun getTopGenres(): List<GenreStat> {
        val (h, v) = userIdHeader()
        return httpClient.get("$baseUrl/history/top-genres") { header(h, v) }.body()
    }

    suspend fun addHistory(request: AddHistoryRequest) {
        val (h, v) = userIdHeader()
        httpClient.post("$baseUrl/history") {
            header(h, v)
            contentType(ContentType.Application.Json)
            setBody(request)
        }
    }

    suspend fun markSkipped(videoId: String) {
        val (h, v) = userIdHeader()
        httpClient.patch("$baseUrl/history/$videoId/skip") { header(h, v) }
    }

    suspend fun getRecommendations(limit: Int = 20): RecommendationResponse {
        val (h, v) = userIdHeader()
        return httpClient.get("$baseUrl/recommendation") {
            header(h, v)
            parameter("limit", limit)
        }.body()
    }

    suspend fun excludeRecommendation(request: ExcludeRequest) {
        val (h, v) = userIdHeader()
        httpClient.post("$baseUrl/recommendation/exclude") {
            header(h, v)
            contentType(ContentType.Application.Json)
            setBody(request)
        }
    }

    suspend fun getPlaylists(): List<Playlist> {
        val (h, v) = userIdHeader()
        return httpClient.get("$baseUrl/playlists") { header(h, v) }.body()
    }

    suspend fun createPlaylist(request: CreatePlaylistRequest): Playlist {
        val (h, v) = userIdHeader()
        return httpClient.post("$baseUrl/playlists") {
            header(h, v)
            contentType(ContentType.Application.Json)
            setBody(request)
        }.body()
    }

    suspend fun deletePlaylist(id: Int) {
        val (h, v) = userIdHeader()
        httpClient.delete("$baseUrl/playlists/$id") { header(h, v) }
    }

    suspend fun addTrackToPlaylist(playlistId: Int, request: AddToPlaylistRequest) {
        val (h, v) = userIdHeader()
        httpClient.post("$baseUrl/playlists/$playlistId/tracks") {
            header(h, v)
            contentType(ContentType.Application.Json)
            setBody(request)
        }
    }

    suspend fun removeTrackFromPlaylist(playlistId: Int, videoId: String) {
        val (h, v) = userIdHeader()
        httpClient.delete("$baseUrl/playlists/$playlistId/tracks/$videoId") { header(h, v) }
    }
}
