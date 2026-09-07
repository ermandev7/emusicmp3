package com.emusic.shared.api

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable

/**
 * Fase 2 de la migración a Kotlin Multiplatform: copia de
 * com.emusic.app.data.api.Models.kt (módulo `app`, Android) portada a
 * commonMain con kotlinx.serialization en vez de Gson (Gson usa reflexión JVM,
 * no disponible en Kotlin/Native/iOS). El módulo `app` todavía usa su propia
 * copia con Gson — Fase 4 hace el cambio real (borra la copia de `app` y
 * actualiza los imports) una vez migrada también la capa de red (Fase 3).
 */
@Serializable
data class Track(
    val type: String = "stream",
    val title: String = "",
    @SerialName("uploaderName") val uploaderName: String? = null,
    @SerialName("uploader") val uploader: String? = null,
    @SerialName("artist") val artist: String? = null,
    @SerialName("author") val author: String? = null,
    @SerialName("thumbnail") val thumbnail: String? = null,
    @SerialName("thumbnailUrl") val thumbnailUrl: String? = null,
    val duration: Int = 0,
    val url: String = "",
    @SerialName("videoId") val videoIdFromJson: String? = null,
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

    val hqThumbnail: String get() = upgradeThumbnailTo("maxresdefault")

    /**
     * Miniatura de calidad media-alta que SÍ existe siempre en YouTube (480×360),
     * a diferencia de maxresdefault (1280×720) que da 404 en muchos vídeos. Se usa como
     * base instantánea en el reproductor para que nunca se vea blanco ni pixelado.
     */
    val sdThumbnail: String get() = upgradeThumbnailTo("hqdefault")

    /** Calidad alta intermedia (640×480). Suele existir; mejor respaldo si maxres da 404. */
    val sddThumbnail: String get() = upgradeThumbnailTo("sddefault")

    /** Reescribe la URL de miniatura a la resolución [quality] de YouTube. */
    private fun upgradeThumbnailTo(quality: String): String {
        val thumb = displayThumbnail
        // 1) URL estándar de YouTube (i.ytimg.com/.../mqdefault.jpg) → reescribir la resolución.
        if (thumb.isNotEmpty()) {
            listOf("default", "mqdefault", "hqdefault", "sddefault", "maxresdefault").forEach { q ->
                val token = "/$q."
                val i = thumb.indexOf(token)
                if (i >= 0) return thumb.substring(0, i) + "/$quality" + thumb.substring(i + token.length - 1)
            }
        }
        // 2) videoId real (11 chars) → URL HD de YouTube (búsqueda/streaming).
        val vid = videoId
        if (vid.length == 11) return "https://i.ytimg.com/vi/$vid/$quality.jpg"
        // 3) Miniatura proxy de googleusercontent (=w120-h120-...): subir el tamaño en píxeles.
        //    Cubre las DESCARGAS (su videoId es un content:// uri y no se puede reconstruir),
        //    incluidas las antiguas guardadas en 120x120.
        if (thumb.isNotEmpty()) {
            val sizeRegex = Regex("=w\\d+-h\\d+")
            if (sizeRegex.containsMatchIn(thumb)) {
                val px = when (quality) {
                    "maxresdefault" -> 720
                    "sddefault" -> 600
                    else -> 480
                }
                return sizeRegex.replace(thumb, "=w$px-h$px")
            }
        }
        // 4) Último recurso: lo que haya.
        return thumb
    }
}

@Serializable
data class SearchResponse(val items: List<Track> = emptyList())

@Serializable
data class StreamInfo(
    val title: String = "",
    val uploader: String = "",
    val thumbnailUrl: String = "",
    val audioStreams: List<AudioStream> = emptyList(),
    val relatedStreams: List<Track> = emptyList()
)

@Serializable
data class AudioStream(
    val url: String = "",
    val bitrate: Int = 0,
    val quality: String = "",
    val mimeType: String = ""
)

@Serializable
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

@Serializable
data class GenreStat(
    val genre: String = "",
    val count: Int = 0
)

@Serializable
data class PrefetchRequest(val videoIds: List<String>)

@Serializable
data class CreatePlaylistRequest(val name: String)

@Serializable
data class AddToPlaylistRequest(
    val videoId: String,
    val title: String,
    val uploaderName: String,
    val thumbnail: String,
    val duration: Int,
    val url: String,
    val type: String = "stream"
)

@Serializable
data class AddFavoriteRequest(
    val title: String,
    val artist: String,
    val thumbnailUrl: String,
    val duration: Int,
    val videoId: String
)

@Serializable
data class AddHistoryRequest(
    val title: String,
    val artist: String,
    val thumbnailUrl: String,
    val duration: Int,
    val videoId: String,
    val isDownloaded: Boolean = false
)

@Serializable
data class RecommendationResponse(val items: List<Track> = emptyList())

@Serializable
data class ExcludeRequest(
    val videoId: String,
    val artist: String = "",
    val title: String = ""
)

/**
 * Forma que devuelve el endpoint /favorites: el `id` es el videoId (String), no el
 * `id: Int` de [Track]. Se mapea a Track para reutilizar la UI/lógica compartida.
 */
@Serializable
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

@Serializable
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
