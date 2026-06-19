package com.emusic.app.data.download

import android.content.ContentUris
import android.content.Context
import android.content.IntentSender
import android.net.Uri
import android.os.Build
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

/** Resultado de intentar borrar una descarga. */
sealed interface DeleteOutcome {
    /** Borrada directamente (archivo propio). */
    object Deleted : DeleteOutcome
    /** No se pudo (error). */
    object Failed : DeleteOutcome
    /** Archivo no creado por esta instalación → el sistema pide confirmación. */
    data class NeedsConsent(val intentSender: IntentSender) : DeleteOutcome
}

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
     * Borra una descarga del almacenamiento (fila de MediaStore + archivo físico).
     * Para archivos que esta instalación creó, se borra directamente. Para archivos de
     * instalaciones anteriores (no propios), el sistema pide confirmación → NeedsConsent.
     */
    suspend fun deleteDownload(uri: String): DeleteOutcome = withContext(Dispatchers.IO) {
        val u = Uri.parse(uri)
        try {
            val rows = context.contentResolver.delete(u, null, null)
            android.util.Log.d("DownloadsRepo", "deleteDownload uri=$uri rows=$rows")
            if (rows > 0) {
                metadataStore.remove(uri)
                DeleteOutcome.Deleted
            } else DeleteOutcome.Failed
        } catch (e: SecurityException) {
            // Archivo no creado por esta instalación: en API 30+ se pide confirmación al
            // usuario con un diálogo del sistema; al aceptar se borra de verdad.
            android.util.Log.w("DownloadsRepo", "deleteDownload requiere consentimiento: ${e.message}")
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                try {
                    val pi = MediaStore.createDeleteRequest(context.contentResolver, listOf(u))
                    DeleteOutcome.NeedsConsent(pi.intentSender)
                } catch (e2: Exception) {
                    android.util.Log.e("DownloadsRepo", "createDeleteRequest falló: ${e2.message}")
                    DeleteOutcome.Failed
                }
            } else DeleteOutcome.Failed
        } catch (e: Exception) {
            android.util.Log.e("DownloadsRepo", "deleteDownload falló uri=$uri: ${e.message}", e)
            DeleteOutcome.Failed
        }
    }

    /** Limpia los metadatos tras un borrado confirmado por el usuario (flujo NeedsConsent). */
    suspend fun confirmDeleted(uri: String) = metadataStore.remove(uri)

    suspend fun getDownloads(): List<DownloadedTrack> = withContext(Dispatchers.IO) {
        val meta = metadataStore.getAll()
        val result = mutableListOf<DownloadedTrack>()
        // Audio en Music/eMusic (mp3/m4a/ogg).
        val audio = query(
            collection = MediaStore.Audio.Media.EXTERNAL_CONTENT_URI,
            titleCol = MediaStore.Audio.Media.TITLE,
            artistCol = MediaStore.Audio.Media.ARTIST,
            meta = meta
        )
        // Otros (webm/opus) guardados en Download/eMusic.
        val downloads = query(
            collection = MediaStore.Downloads.EXTERNAL_CONTENT_URI,
            titleCol = MediaStore.MediaColumns.DISPLAY_NAME,
            artistCol = null,
            meta = meta
        )
        result += audio
        result += downloads
        android.util.Log.d("DownloadsRepo", "getDownloads audio=${audio.size} downloads=${downloads.size} total=${result.size}")
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
