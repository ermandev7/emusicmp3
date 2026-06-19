package com.emusic.app.ui.player

import android.content.ContentValues
import android.content.Context
import android.os.Environment
import android.provider.MediaStore
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.emusic.app.data.api.Track
import com.emusic.app.data.api.bestAudioStream
import com.emusic.app.data.download.DownloadMeta
import com.emusic.app.data.download.DownloadMetadataStore
import com.emusic.app.data.download.DownloadTarget
import com.emusic.app.data.repository.MusicRepository
import com.emusic.app.player.MusicController
import com.emusic.app.player.PlayerState
import dagger.hilt.android.lifecycle.HiltViewModel
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.HttpURLConnection
import java.net.URL
import javax.inject.Inject

data class PlayerUiExtras(
    val isFavorite: Boolean = false,
    val isDownloading: Boolean = false,
    val downloadDone: Boolean = false
)

@HiltViewModel
class PlayerViewModel @Inject constructor(
    private val controller: MusicController,
    private val repository: MusicRepository,
    private val downloadMetadata: DownloadMetadataStore,
    @ApplicationContext private val context: Context
) : ViewModel() {

    val state: StateFlow<PlayerState> = controller.state

    private val _extras = MutableStateFlow(PlayerUiExtras())
    val extras: StateFlow<PlayerUiExtras> = _extras.asStateFlow()

    // Track resolviéndose (URL aún no lista). Vive en el MusicController (singleton)
    // para que el skeleton se vea aunque se inicie la reproducción desde otra pantalla.
    val loadingTrack: StateFlow<Track?> = controller.loadingTrack

    private var lastCheckedVideoId: String? = null

    fun checkFavorite(videoId: String) {
        if (videoId == lastCheckedVideoId) return
        lastCheckedVideoId = videoId
        viewModelScope.launch {
            val isFav = repository.isFavorite(videoId)
            _extras.value = _extras.value.copy(isFavorite = isFav, downloadDone = false)
        }
    }

    fun toggleFavorite() {
        val track = state.value.currentTrack ?: return
        viewModelScope.launch {
            if (_extras.value.isFavorite) {
                repository.removeFavorite(track.videoId)
                _extras.value = _extras.value.copy(isFavorite = false)
            } else {
                repository.addFavorite(track)
                _extras.value = _extras.value.copy(isFavorite = true)
            }
        }
    }

    fun downloadTrack() {
        val track = state.value.currentTrack ?: return
        if (_extras.value.isDownloading) return
        _extras.value = _extras.value.copy(isDownloading = true, downloadDone = false)
        viewModelScope.launch {
            val ok = withContext(Dispatchers.IO) { downloadToMediaStore(track) }
            if (ok) {
                repository.addHistory(track, isDownloaded = true)
                _extras.value = _extras.value.copy(isDownloading = false, downloadDone = true)
            } else {
                _extras.value = _extras.value.copy(isDownloading = false)
            }
        }
    }

    /** Descarga el mejor audio a la carpeta Music/eMusic. Devuelve true si tuvo éxito. */
    private suspend fun downloadToMediaStore(track: Track): Boolean {
        val resolver = context.contentResolver
        var uri: android.net.Uri? = null
        return try {
            val streamInfo = repository.getStream(track.videoId)
            val best = streamInfo?.bestAudioStream()
            if (best == null || best.url.isEmpty()) {
                android.util.Log.e("PlayerVM", "Descarga: sin audioStream para ${track.videoId}")
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
                    android.util.Log.e("PlayerVM", "Descarga: MediaStore.insert devolvió null")
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
                android.util.Log.e("PlayerVM", "Descarga: HTTP $code para ${track.videoId}")
                resolver.delete(uri, null, null)
                uri = null
                return false
            }
            resolver.openOutputStream(uri)?.use { out ->
                conn.inputStream.use { input -> input.copyTo(out) }
            }
            conn.disconnect()

            // Marcar como completo y visible.
            values.clear()
            values.put(MediaStore.MediaColumns.IS_PENDING, 0)
            resolver.update(uri, values, null, null)

            // Guardar artista + carátula (MediaStore no los conserva bien) indexados por
            // la URI de contenido (estable; coincide con la que reconstruye DownloadsRepository).
            downloadMetadata.save(
                uri.toString(),
                DownloadMeta(track.title, track.displayArtist, track.displayThumbnail)
            )

            android.util.Log.d("PlayerVM", "Descarga OK: $safeName.${target.extension}")
            true
        } catch (e: Exception) {
            android.util.Log.e("PlayerVM", "Descarga falló: ${e.message}", e)
            // Borrar el archivo pendiente para no dejar huérfanos/duplicados.
            uri?.let { runCatching { resolver.delete(it, null, null) } }
            false
        }
    }

    fun playTrack(track: Track, queue: List<Track> = emptyList()) {
        lastCheckedVideoId = null
        _extras.value = PlayerUiExtras()
        // El controller marca loadingTrack (skeleton) y resuelve la URL bajo demanda.
        controller.playTrack(track, queue)

        viewModelScope.launch {
            repository.addHistory(track)
            if (queue.size > 1) {
                repository.prefetch(queue.drop(1).take(3).map { it.videoId })
            }
        }
    }

    fun playDownloads(tracks: List<com.emusic.app.data.download.DownloadedTrack>, index: Int) =
        controller.playLocal(tracks, index)

    fun seekToIndex(index: Int) = controller.seekToIndex(index)
    fun moveQueueItem(from: Int, to: Int) = controller.moveQueueItem(from, to)
    fun removeQueueItem(index: Int) = controller.removeQueueItem(index)
    fun retry() = controller.retry()

    fun play() = controller.play()
    fun pause() = controller.pause()
    fun next() = controller.seekToNext()
    fun previous() = controller.seekToPrevious()
    fun seekTo(posMs: Long) = controller.seekTo(posMs)
    fun toggleShuffle() = controller.toggleShuffle()
    fun cycleRepeat() = controller.cycleRepeatMode()

    fun getPosition(): Long = controller.getPositionMs()
    fun getDuration(): Long = controller.getDurationMs()

    fun markSkipped() {
        viewModelScope.launch {
            state.value.currentTrack?.videoId?.let { repository.markSkipped(it) }
        }
    }
}
