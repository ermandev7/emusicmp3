package com.emusic.app.data.api

import com.google.gson.annotations.SerializedName

data class Track(
    val type: String = "stream",
    val title: String = "",
    @SerializedName("uploaderName") val uploaderName: String? = null,
    @SerializedName("uploader") val uploader: String? = null,
    @SerializedName("artist") val artist: String? = null,
    @SerializedName("author") val author: String? = null,
    @SerializedName("thumbnail") val thumbnail: String? = null,
    @SerializedName("thumbnailUrl") val thumbnailUrl: String? = null,
    val duration: Int = 0,
    val url: String = "",
    @SerializedName("videoId") val videoIdFromJson: String? = null,
    val id: Int = 0
) {
    val displayArtist: String
        get() = uploaderName ?: uploader ?: artist ?: author ?: ""

    val displayThumbnail: String
        get() = thumbnail ?: thumbnailUrl ?: ""

    val videoId: String
        get() {
            if (!videoIdFromJson.isNullOrEmpty()) return videoIdFromJson
            val idx = url.indexOf("?v=")
            if (idx >= 0) {
                val raw = url.substring(idx + 3)
                // Cortar si hay más parámetros (&list=..., &t=..., etc.)
                val end = raw.indexOf('&')
                return if (end >= 0) raw.substring(0, end) else raw
            }
            return url.trimStart('/')
        }

    val hqThumbnail: String
        get() {
            val thumb = displayThumbnail
            if (thumb.isNotEmpty()) {
                listOf("default", "mqdefault", "hqdefault", "sddefault").forEach { q ->
                    val token = "/$q."
                    val i = thumb.indexOf(token)
                    if (i >= 0) return thumb.substring(0, i) + "/maxresdefault" + thumb.substring(i + token.length - 1)
                }
            }
            val vid = videoId
            return if (vid.isNotEmpty()) "https://i.ytimg.com/vi/$vid/maxresdefault.jpg" else thumb
        }
}

data class SearchResponse(val items: List<Track> = emptyList())

data class StreamInfo(
    val title: String = "",
    val uploader: String = "",
    val thumbnailUrl: String = "",
    val audioStreams: List<AudioStream> = emptyList(),
    val relatedStreams: List<Track> = emptyList()
)

data class AudioStream(
    val url: String = "",
    val bitrate: Int = 0,
    val quality: String = "",
    val mimeType: String = ""
)

data class Playlist(
    val id: Int = 0,
    val name: String = "",
    val description: String? = null,
    val songsJson: String = "[]",
    val tracks: List<Track> = emptyList()
) {
    val trackCount: Int get() = tracks.size
    val coverUrl: String? get() = tracks.firstOrNull { it.displayThumbnail.isNotEmpty() }?.displayThumbnail
}

data class GenreStat(
    val genre: String = "",
    val count: Int = 0
)

data class PrefetchRequest(val videoIds: List<String>)

data class CreatePlaylistRequest(val name: String)

data class AddToPlaylistRequest(
    val videoId: String,
    val title: String,
    val uploaderName: String,
    val thumbnail: String,
    val duration: Int,
    val url: String,
    val type: String = "stream"
)

data class AddFavoriteRequest(
    val title: String,
    val artist: String,
    val thumbnailUrl: String,
    val duration: Int,
    val videoId: String
)

data class AddHistoryRequest(
    val title: String,
    val artist: String,
    val thumbnailUrl: String,
    val duration: Int,
    val videoId: String,
    val isDownloaded: Boolean = false
)

data class RecommendationResponse(val items: List<Track> = emptyList())

data class ExcludeRequest(
    val videoId: String,
    val artist: String = "",
    val title: String = ""
)

/**
 * Forma que devuelve el endpoint /favorites: el `id` es el videoId (String), no el
 * `id: Int` de [Track]. Se mapea a Track para reutilizar la UI. Sin este DTO, Gson
 * falla al parsear "id":"abc123" en el Int de Track y la lista sale vacía.
 */
data class FavoriteEntry(
    val id: String = "",
    val title: String = "",
    val artist: String = "",
    val thumbnailUrl: String = "",
    val duration: Int = 0,
    val userId: String = ""
) {
    fun toTrack(): Track = Track(
        title = title,
        artist = artist,
        thumbnailUrl = thumbnailUrl,
        duration = duration,
        videoIdFromJson = id
    )
}

data class HistoryEntry(
    val id: Int = 0,
    val videoId: String = "",
    val title: String = "",
    val artist: String = "",
    val thumbnailUrl: String = "",
    val duration: Int = 0,
    val playCount: Int = 1,
    val playedAt: String = "",
    val userId: String = "",
    val skippedEarly: Boolean = false,
    val isDownloaded: Boolean = false
) {
    fun toTrack(): Track = Track(
        title = title,
        artist = artist,
        thumbnailUrl = thumbnailUrl,
        duration = duration,
        videoIdFromJson = videoId,
        id = id
    )
}
