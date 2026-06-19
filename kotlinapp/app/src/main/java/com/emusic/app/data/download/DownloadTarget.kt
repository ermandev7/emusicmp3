package com.emusic.app.data.download

/**
 * Resultado de decidir cómo guardar una descarga: extensión, MIME y si va a la
 * carpeta de Música (MediaStore.Audio, que solo acepta ciertos formatos) o a la
 * de Descargas (acepta cualquiera, p. ej. webm/opus de YouTube).
 *
 * Lógica pura sin dependencias de Android para poder testearla en JVM.
 */
data class DownloadTarget(
    val extension: String,
    val mimeType: String,
    /** true → MediaStore.Audio (Music/eMusic); false → MediaStore.Downloads (Download/eMusic). */
    val toMusicFolder: Boolean
) {
    companion object {
        // MIME que MediaStore.Audio acepta (rechaza audio/webm con IllegalArgumentException).
        private val AUDIO_COMPATIBLE = setOf(
            "audio/mpeg", "audio/mp4", "audio/aac", "audio/ogg", "audio/flac", "audio/x-wav"
        )

        /** Decide extensión, MIME final y carpeta a partir del MIME del stream. */
        fun forStreamMime(streamMime: String): DownloadTarget {
            val (ext, mime) = when {
                streamMime.contains("webm") -> "webm" to "audio/webm"
                streamMime.contains("mp4") || streamMime.contains("m4a") -> "m4a" to "audio/mp4"
                streamMime.contains("mpeg") -> "mp3" to "audio/mpeg"
                streamMime.contains("ogg") || streamMime.contains("opus") -> "ogg" to "audio/ogg"
                else -> "m4a" to "audio/mp4"
            }
            return DownloadTarget(ext, mime, toMusicFolder = mime in AUDIO_COMPATIBLE)
        }
    }
}
