package com.emusic.app.data.download

import android.content.ContentUris
import android.content.Context
import android.net.Uri
import android.provider.MediaStore
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import javax.inject.Inject
import javax.inject.Singleton

/** Una pista descargada localmente (en Music/eMusic o Download/eMusic). */
data class DownloadedTrack(
    val uri: String,          // content:// reproducible directamente (sin API)
    val title: String,
    val artist: String,
    val durationMs: Long,
    val thumbnailUrl: String = ""
)

/**
 * Lee los archivos descargados por eMusic desde MediaStore. La reproducción es
 * 100% local: no toca la API ni la red.
 */
@Singleton
class DownloadsRepository @Inject constructor(
    @ApplicationContext private val context: Context,
    private val metadataStore: DownloadMetadataStore
) {
    /**
     * Borra una descarga del almacenamiento. eMusic creó estos archivos vía MediaStore,
     * así que es el propietario y puede eliminarlos directamente (sin consentimiento extra).
     * Devuelve true si se borró una fila.
     */
    suspend fun deleteDownload(uri: String): Boolean = withContext(Dispatchers.IO) {
        try {
            metadataStore.remove(uri) // los metadatos están indexados por la URI
            context.contentResolver.delete(Uri.parse(uri), null, null) > 0
        } catch (_: Exception) {
            false
        }
    }

    suspend fun getDownloads(): List<DownloadedTrack> = withContext(Dispatchers.IO) {
        val meta = metadataStore.getAll()
        val result = mutableListOf<DownloadedTrack>()
        // Audio en Music/eMusic (mp3/m4a/ogg).
        result += query(
            collection = MediaStore.Audio.Media.EXTERNAL_CONTENT_URI,
            titleCol = MediaStore.Audio.Media.TITLE,
            artistCol = MediaStore.Audio.Media.ARTIST,
            meta = meta
        )
        // Otros (webm/opus) guardados en Download/eMusic.
        result += query(
            collection = MediaStore.Downloads.EXTERNAL_CONTENT_URI,
            titleCol = MediaStore.MediaColumns.DISPLAY_NAME,
            artistCol = null,
            meta = meta
        )
        result.sortedBy { it.title.lowercase() }
    }

    private fun query(
        collection: Uri,
        titleCol: String,
        artistCol: String?,
        meta: Map<String, DownloadMeta>
    ): List<DownloadedTrack> {
        val items = mutableListOf<DownloadedTrack>()
        val projection = buildList {
            add(MediaStore.MediaColumns._ID)
            add(titleCol)
            if (artistCol != null) add(artistCol)
            add(MediaStore.MediaColumns.DURATION)
        }.toTypedArray()
        val selection = "${MediaStore.MediaColumns.RELATIVE_PATH} LIKE ?"
        val args = arrayOf("%eMusic%")

        try {
            context.contentResolver.query(collection, projection, selection, args, null)?.use { c ->
                val idIdx = c.getColumnIndexOrThrow(MediaStore.MediaColumns._ID)
                val titleIdx = c.getColumnIndexOrThrow(titleCol)
                val artistIdx = artistCol?.let { c.getColumnIndex(it) } ?: -1
                val durIdx = c.getColumnIndex(MediaStore.MediaColumns.DURATION)
                while (c.moveToNext()) {
                    val id = c.getLong(idIdx)
                    val uri = ContentUris.withAppendedId(collection, id).toString()
                    // Metadatos guardados al descargar (título/artista/carátula), por URI.
                    val m = meta[uri]
                    // Quitar extensión y el sufijo " (1)" que MediaStore añade a duplicados.
                    val fileTitle = (c.getString(titleIdx) ?: "Desconocido")
                        .replace(Regex("""\.(webm|m4a|mp3|ogg)( \(\d+\))?$""", RegexOption.IGNORE_CASE), "")
                        .trim()
                    val mediaArtist = if (artistIdx >= 0) c.getString(artistIdx).orEmpty() else ""
                    val dur = if (durIdx >= 0) c.getLong(durIdx) else 0L
                    items += DownloadedTrack(
                        uri = uri,
                        title = m?.title?.takeIf { it.isNotBlank() } ?: fileTitle,
                        artist = m?.artist?.takeIf { it.isNotBlank() } ?: mediaArtist,
                        durationMs = dur,
                        thumbnailUrl = m?.thumbnailUrl.orEmpty()
                    )
                }
            }
        } catch (_: Exception) {
            // Colección no disponible o sin permiso → lista vacía para esa fuente.
        }
        return items
    }
}
