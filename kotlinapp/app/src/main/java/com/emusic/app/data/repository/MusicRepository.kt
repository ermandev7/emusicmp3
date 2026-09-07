package com.emusic.app.data.repository

import com.emusic.app.data.api.*
import com.emusic.shared.network.EMusicNetworkClient
import com.emusic.shared.network.MusicApiClient
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Fase 4 KMP: antes usaba Retrofit (ApiService) + un fallback a instancias públicas de
 * Piped (FallbackApiService) armados a mano acá. Ahora delega toda esa lógica a
 * `shared` (EMusicNetworkClient/MusicApiClient, con Ktor) — el mismo código que la app
 * de iPhone va a compartir. La API pública de esta clase (tipos y comportamiento) no
 * cambia: sigue devolviendo los modelos de com.emusic.app.data.api de siempre, así que
 * ningún ViewModel ni el MusicService necesitan tocarse.
 */
@Singleton
class MusicRepository @Inject constructor(
    private val network: EMusicNetworkClient,
    private val api: MusicApiClient
) {

    suspend fun search(query: String): List<Track> = withContext(Dispatchers.IO) {
        network.search(query).map { it.toAppTrack() }
    }

    suspend fun getStream(videoId: String): StreamInfo? = withContext(Dispatchers.IO) {
        network.getStream(videoId)?.toAppStreamInfo()
    }

    suspend fun prefetch(videoIds: List<String>) {
        try { api.prefetch(videoIds.take(3)) } catch (_: Exception) {}
    }

    suspend fun getTrending(): List<Track> = withContext(Dispatchers.IO) {
        try {
            api.getTrending().items.filter { it.title.isNotEmpty() }.map { it.toAppTrack() }
        } catch (_: Exception) { emptyList() }
    }

    suspend fun getFavorites(): List<Track> = withContext(Dispatchers.IO) {
        try { api.getFavorites().map { it.toAppTrack() } } catch (_: Exception) { emptyList() }
    }

    suspend fun addFavorite(track: Track) {
        try {
            api.addFavorite(
                com.emusic.shared.api.AddFavoriteRequest(
                    title = track.title,
                    artist = track.displayArtist,
                    thumbnailUrl = track.displayThumbnail,
                    duration = track.duration,
                    videoId = track.videoId
                )
            )
        } catch (_: Exception) {}
    }

    suspend fun removeFavorite(videoId: String) {
        try { api.removeFavorite(videoId) } catch (_: Exception) {}
    }

    suspend fun isFavorite(videoId: String): Boolean = withContext(Dispatchers.IO) {
        try { api.isFavorite(videoId) } catch (_: Exception) { false }
    }

    suspend fun getHistory(): List<Track> = withContext(Dispatchers.IO) {
        try { api.getHistory().map { it.toAppTrack() } } catch (_: Exception) { emptyList() }
    }

    suspend fun getMostPlayed(limit: Int = 20): List<Track> = withContext(Dispatchers.IO) {
        try {
            api.getHistory()
                .sortedByDescending { it.playCount }
                .take(limit)
                .map { it.toAppTrack() }
        } catch (_: Exception) { emptyList() }
    }

    suspend fun getTopGenres(): List<GenreStat> = withContext(Dispatchers.IO) {
        try { api.getTopGenres().map { it.toAppGenreStat() } } catch (_: Exception) { emptyList() }
    }

    suspend fun addHistory(track: Track, isDownloaded: Boolean = false) {
        try {
            api.addHistory(
                com.emusic.shared.api.AddHistoryRequest(
                    title = track.title,
                    artist = track.displayArtist,
                    thumbnailUrl = track.displayThumbnail,
                    duration = track.duration,
                    videoId = track.videoId,
                    isDownloaded = isDownloaded
                )
            )
        } catch (_: Exception) {}
    }

    suspend fun markSkipped(videoId: String) {
        try { api.markSkipped(videoId) } catch (_: Exception) {}
    }

    suspend fun getRecommendations(limit: Int = 20): List<Track> = withContext(Dispatchers.IO) {
        try { api.getRecommendations(limit).items.map { it.toAppTrack() } } catch (_: Exception) { emptyList() }
    }

    suspend fun excludeRecommendation(track: Track) {
        try {
            api.excludeRecommendation(
                com.emusic.shared.api.ExcludeRequest(
                    videoId = track.videoId,
                    artist = track.displayArtist,
                    title = track.title
                )
            )
        } catch (_: Exception) {}
    }

    suspend fun getPlaylists(): List<Playlist> = withContext(Dispatchers.IO) {
        try { api.getPlaylists().map { it.toAppPlaylist() } } catch (_: Exception) { emptyList() }
    }

    suspend fun createPlaylist(name: String): Playlist? = withContext(Dispatchers.IO) {
        try { api.createPlaylist(com.emusic.shared.api.CreatePlaylistRequest(name)).toAppPlaylist() } catch (_: Exception) { null }
    }

    suspend fun deletePlaylist(id: Int) {
        try { api.deletePlaylist(id) } catch (_: Exception) {}
    }

    suspend fun addTrackToPlaylist(playlistId: Int, track: Track) {
        try {
            api.addTrackToPlaylist(
                playlistId,
                com.emusic.shared.api.AddToPlaylistRequest(
                    videoId = track.videoId,
                    title = track.title,
                    uploaderName = track.displayArtist,
                    thumbnail = track.displayThumbnail,
                    duration = track.duration,
                    url = track.url
                )
            )
        } catch (_: Exception) {}
    }

    suspend fun removeTrackFromPlaylist(playlistId: Int, videoId: String) {
        try { api.removeTrackFromPlaylist(playlistId, videoId) } catch (_: Exception) {}
    }
}
