package com.emusic.app.data.download

import android.content.ContentValues
import android.content.Context
import android.os.Environment
import android.provider.MediaStore
import com.emusic.app.data.api.Track
import com.emusic.app.data.api.bestAudioStream
import com.emusic.app.data.repository.MusicRepository
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import java.net.HttpURLConnection
import java.net.URL
import javax.inject.Inject
import javax.inject.Singleton

/** Estado global de la descarga en curso (una a la vez; uso personal). */
data class DownloadState(
    val videoId: String? = null,
    val title: String = "",
    val artist: String = "",
    val thumbnailUrl: String = "",
    val isDownloading: Boolean = false,
    /** 0..100. -1 = en curso pero sin tamaño conocido (indeterminado). */
    val progress: Int = 0,
    /** Último videoId descargado con éxito (para mostrar el check). */
    val lastDoneVideoId: String? = null
)

/**
 * Gestiona las descargas en un scope a nivel de aplicación, NO en el viewModelScope
 * de una pantalla. Así la descarga sobrevive a la navegación: antes corría en la
 * PlayerViewModel y, al cambiar de pantalla, esa VM se destruía, se cancelaba la
 * corrutina y el archivo a medias se borraba ("se perdía la descarga").
 */
@Singleton
class DownloadManager @Inject constructor(
    @ApplicationContext private val context: Context,
    private val repository: MusicRepository,
    private val metadataStore: DownloadMetadataStore
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    private val _state = MutableStateFlow(DownloadState())
    val state: StateFlow<DownloadState> = _state.asStateFlow()

    /** Inicia la descarga del track. Si ya hay una en curso, no hace nada. */
    fun download(track: Track) {
        if (_state.value.isDownloading) return
        _state.value = DownloadState(
            videoId = track.videoId,
            title = track.title,
            artist = track.displayArtist,
            thumbnailUrl = track.displayThumbnail,
            isDownloading = true,
            progress = 0
        )
        scope.launch {
            val ok = downloadToMediaStore(track) { pct ->
                _state.update { if (it.videoId == track.videoId) it.copy(progress = pct) else it }
            }
            if (ok) repository.addHistory(track, isDownloaded = true)
            _state.value = DownloadState(lastDoneVideoId = if (ok) track.videoId else null)
        }
    }

    /** Descarga el mejor audio a la carpeta Music/eMusic. Devuelve true si tuvo éxito. */
    private suspend fun downloadToMediaStore(track: Track, onProgress: (Int) -> Unit): Boolean {
        val resolver = context.contentResolver
        var uri: android.net.Uri? = null
        return try {
            val streamInfo = repository.getStream(track.videoId)
            val best = streamInfo?.bestAudioStream()
            if (best == null || best.url.isEmpty()) {
                android.util.Log.e("DownloadManager", "Sin audioStream para ${track.videoId}")
                return false
            }

            // Decidir extensión, MIME y carpeta (lógica pura, testeable).
            val target = DownloadTarget.forStreamMime(best.mimeType)
            val safeName = track.title.replace(Regex("[^a-zA-Z0-9áéíóúñÁÉÍÓÚÑ _-]"), "").take(80)
                .ifBlank { track.videoId }

            val collection = if (target.toMusicFolder)
                MediaStore.Audio.Media.EXTERNAL_CONTENT_URI
            else
                MediaStore.Downloads.EXTERNAL_CONTENT_URI

            val values = ContentValues().apply {
                put(MediaStore.MediaColumns.DISPLAY_NAME, "$safeName.${target.extension}")
                put(MediaStore.MediaColumns.MIME_TYPE, target.mimeType)
                put(MediaStore.MediaColumns.IS_PENDING, 1) // escritura atómica
                if (target.toMusicFolder) {
                    put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_MUSIC + "/eMusic")
                    put(MediaStore.Audio.Media.TITLE, track.title)
                    put(MediaStore.Audio.Media.ARTIST, track.displayArtist)
                } else {
                    put(MediaStore.MediaColumns.RELATIVE_PATH, Environment.DIRECTORY_DOWNLOADS + "/eMusic")
                }
            }
            uri = resolver.insert(collection, values)
                ?: run {
                    android.util.Log.e("DownloadManager", "MediaStore.insert devolvió null")
                    return false
                }

            // googlevideo rechaza peticiones sin User-Agent de navegador → usar HttpURLConnection.
            val conn = (URL(best.url).openConnection() as HttpURLConnection).apply {
                setRequestProperty("User-Agent", "Mozilla/5.0 (Linux; Android) eMusic/1.0")
                connectTimeout = 20_000
                readTimeout = 20_000
                instanceFollowRedirects = true
            }
            val code = conn.responseCode
            if (code !in 200..299) {
                android.util.Log.e("DownloadManager", "HTTP $code para ${track.videoId}")
                resolver.delete(uri, null, null)
                uri = null
                return false
            }
            val total = conn.contentLengthLong
            onProgress(if (total > 0) 0 else -1)
            resolver.openOutputStream(uri)?.use { out ->
                conn.inputStream.use { input ->
                    val buffer = ByteArray(64 * 1024)
                    var downloaded = 0L
                    var lastPct = 0
                    while (true) {
                        val read = input.read(buffer)
                        if (read < 0) break
                        out.write(buffer, 0, read)
                        downloaded += read
                        if (total > 0) {
                            val pct = ((downloaded * 100) / total).toInt().coerceIn(0, 100)
                            if (pct != lastPct) { lastPct = pct; onProgress(pct) }
                        }
                    }
                }
            }
            conn.disconnect()
            onProgress(100)

            // Marcar como completo y visible.
            values.clear()
            values.put(MediaStore.MediaColumns.IS_PENDING, 0)
            resolver.update(uri, values, null, null)

            // Guardar artista + carátula (MediaStore no los conserva bien) indexados por
            // la URI de contenido. Guardamos una carátula HD de YouTube (sddefault 640x480)
            // construida desde el videoId real: la miniatura de búsqueda es un proxy de
            // 120x120 que se vería pixelado a pantalla completa. Tras descargar, el videoId
            // pasa a ser un content:// uri y ya no se podría reconstruir.
            val hdThumb = track.sddThumbnail.ifBlank { track.displayThumbnail }
            metadataStore.save(
                uri.toString(),
                DownloadMeta(track.title, track.displayArtist, hdThumb)
            )

            android.util.Log.d("DownloadManager", "Descarga OK: $safeName.${target.extension}")
            true
        } catch (e: Exception) {
            android.util.Log.e("DownloadManager", "Descarga falló: ${e.message}", e)
            // Borrar el archivo pendiente para no dejar huérfanos/duplicados.
            uri?.let { runCatching { resolver.delete(it, null, null) } }
            false
        }
    }
}
