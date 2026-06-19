package com.emusic.app.data.repository

import com.emusic.app.data.api.*
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

@Singleton
class MusicRepository @Inject constructor(
    private val api: ApiService,
    private val fallbackApi: FallbackApiService
) {

    suspend fun search(query: String): List<Track> = withContext(Dispatchers.IO) {
        try {
            val res = api.search(query)
            if (res.isSuccessful) {
                val items = res.body()?.items?.filter {
                    (it.type.isEmpty() || it.type == "stream") && it.title.isNotEmpty()
                } ?: emptyList()
                if (items.isNotEmpty()) return@withContext items
            }
        } catch (_: Exception) {}

        // Fallbacks públicos de Piped
        val fallbacks = listOf(
            "https://pipedapi.kavin.rocks",
            "https://pipedapi.colby.land",
            "https://piped-api.garudalinux.org",
            "https://api.piped.yt"
        )
        for (base in fallbacks) {
            try {
                val res = fallbackApi.search(base, query)
                if (res.isSuccessful) {
                    val items = res.body()?.items?.filter {
                        (it.type.isEmpty() || it.type == "stream") && it.title.isNotEmpty()
                    } ?: emptyList()
                    if (items.isNotEmpty()) return@withContext items
                }
            } catch (_: Exception) {}
        }
        emptyList()
    }

    suspend fun getStream(videoId: String): StreamInfo? = withContext(Dispatchers.IO) {
        try {
            val res = api.getStream(videoId)
            if (res.isSuccessful && res.body()?.audioStreams?.isNotEmpty() == true)
                return@withContext res.body()
        } catch (_: Exception) {}

        val fallbacks = listOf(
            "https://pipedapi.kavin.rocks",
            "https://pipedapi.colby.land",
            "https://piped-api.garudalinux.org",
            "https://api.piped.yt"
        )
        for (base in fallbacks) {
            try {
                val res = fallbackApi.getStream(base, videoId)
                if (res.isSuccessful && res.body()?.audioStreams?.isNotEmpty() == true)
                    return@withContext res.body()
            } catch (_: Exception) {}
        }
        null
    }

    suspend fun prefetch(videoIds: List<String>) {
        try { api.prefetch(PrefetchRequest(videoIds.take(3))) } catch (_: Exception) {}
    }

    suspend fun getTrending(): List<Track> = withContext(Dispatchers.IO) {
        try {
            api.getTrending().body()?.items?.filter { it.title.isNotEmpty() } ?: emptyList()
        } catch (_: Exception) { emptyList() }
    }

    suspend fun getFavorites(): List<Track> = withContext(Dispatchers.IO) {
        try { api.getFavorites().body()?.map { it.toTrack() } ?: emptyList() } catch (_: Exception) { emptyList() }
    }

    suspend fun addFavorite(track: Track) {
        try {
            api.addFavorite(AddFavoriteRequest(
                title = track.title,
                artist = track.displayArtist,
                thumbnailUrl = track.displayThumbnail,
                duration = track.duration,
                videoId = track.videoId
            ))
        } catch (_: Exception) {}
    }

    suspend fun removeFavorite(videoId: String) {
        try { api.removeFavorite(videoId) } catch (_: Exception) {}
    }

    suspend fun isFavorite(videoId: String): Boolean = withContext(Dispatchers.IO) {
        try { api.isFavorite(videoId).isSuccessful } catch (_: Exception) { false }
    }

    suspend fun getHistory(): List<Track> = withContext(Dispatchers.IO) {
        try {
            api.getHistory().body()?.map { it.toTrack() } ?: emptyList()
        } catch (_: Exception) { emptyList() }
    }

    suspend fun getMostPlayed(limit: Int = 20): List<Track> = withContext(Dispatchers.IO) {
        try {
            api.getHistory().body()
                ?.sortedByDescending { it.playCount }
                ?.take(limit)
                ?.map { it.toTrack() }
                ?: emptyList()
        } catch (_: Exception) { emptyList() }
    }

    suspend fun getTopGenres(): List<GenreStat> = withContext(Dispatchers.IO) {
        try { api.getTopGenres().body() ?: emptyList() } catch (_: Exception) { emptyList() }
    }

    suspend fun addHistory(track: Track, isDownloaded: Boolean = false) {
        try {
            api.addHistory(AddHistoryRequest(
                title = track.title,
                artist = track.displayArtist,
                thumbnailUrl = track.displayThumbnail,
                duration = track.duration,
                videoId = track.videoId,
                isDownloaded = isDownloaded
            ))
        } catch (_: Exception) {}
    }

    suspend fun markSkipped(videoId: String) {
        try { api.markSkipped(videoId) } catch (_: Exception) {}
    }

    suspend fun getRecommendations(limit: Int = 20): List<Track> = withContext(Dispatchers.IO) {
        try { api.getRecommendations(limit).body()?.items ?: emptyList() } catch (_: Exception) { emptyList() }
    }

    suspend fun excludeRecommendation(track: Track) {
        try {
            api.excludeRecommendation(
                ExcludeRequest(videoId = track.videoId, artist = track.displayArtist, title = track.title)
            )
        } catch (_: Exception) {}
    }

    suspend fun getPlaylists(): List<Playlist> = withContext(Dispatchers.IO) {
        try { api.getPlaylists().body() ?: emptyList() } catch (_: Exception) { emptyList() }
    }

    suspend fun createPlaylist(name: String): Playlist? = withContext(Dispatchers.IO) {
        try { api.createPlaylist(CreatePlaylistRequest(name)).body() } catch (_: Exception) { null }
    }

    suspend fun deletePlaylist(id: Int) {
        try { api.deletePlaylist(id) } catch (_: Exception) {}
    }

    suspend fun addTrackToPlaylist(playlistId: Int, track: Track) {
        try {
            api.addTrackToPlaylist(playlistId, AddToPlaylistRequest(
                videoId = track.videoId,
                title = track.title,
                uploaderName = track.displayArtist,
                thumbnail = track.displayThumbnail,
                duration = track.duration,
                url = track.url
            ))
        } catch (_: Exception) {}
    }

    suspend fun removeTrackFromPlaylist(playlistId: Int, videoId: String) {
        try { api.removeTrackFromPlaylist(playlistId, videoId) } catch (_: Exception) {}
    }
}
