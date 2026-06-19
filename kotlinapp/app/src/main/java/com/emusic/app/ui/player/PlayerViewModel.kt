package com.emusic.app.ui.player

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.emusic.app.data.api.Track
import com.emusic.app.data.download.DownloadManager
import com.emusic.app.data.repository.MusicRepository
import com.emusic.app.player.MusicController
import com.emusic.app.player.PlayerState
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import javax.inject.Inject

data class PlayerUiExtras(
    val isFavorite: Boolean = false,
    val isDownloading: Boolean = false,
    val downloadDone: Boolean = false,
    /** Progreso de descarga 0..100. -1 = en curso pero sin tamaño conocido (indeterminado). */
    val downloadProgress: Int = 0
)

@HiltViewModel
class PlayerViewModel @Inject constructor(
    private val controller: MusicController,
    private val repository: MusicRepository,
    private val downloadManager: DownloadManager
) : ViewModel() {

    val state: StateFlow<PlayerState> = controller.state

    private val _extras = MutableStateFlow(PlayerUiExtras())
    val extras: StateFlow<PlayerUiExtras> = _extras.asStateFlow()

    // Track resolviéndose (URL aún no lista). Vive en el MusicController (singleton)
    // para que el skeleton se vea aunque se inicie la reproducción desde otra pantalla.
    val loadingTrack: StateFlow<Track?> = controller.loadingTrack

    private var lastCheckedVideoId: String? = null

    init {
        // La descarga vive en un singleton (sobrevive a la navegación). Reflejamos su
        // estado en _extras solo para el track que se está mostrando.
        viewModelScope.launch {
            downloadManager.state.collect { ds ->
                val current = state.value.currentTrack?.videoId
                val isCurrent = current != null && ds.videoId == current
                _extras.update { ex ->
                    ex.copy(
                        isDownloading = ds.isDownloading && isCurrent,
                        downloadProgress = if (ds.isDownloading && isCurrent) ds.progress else 0,
                        downloadDone = ex.downloadDone || ds.lastDoneVideoId == current
                    )
                }
            }
        }
    }

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
        // La descarga corre en el DownloadManager (scope de aplicación), así que sigue
        // aunque el usuario cambie de pantalla. El estado se refleja vía el collector.
        downloadManager.download(track)
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

    fun playDownloads(tracks: List<com.emusic.app.data.download.DownloadedTrack>, index: Int) {
        controller.playLocal(tracks, index)
        // Al terminar las descargas, continuar con recomendadas (como en el resto de la
        // app). getRecommendations falla y devuelve vacío si no hay internet → en ese
        // caso ponemos las descargas en bucle para que la música no pare.
        viewModelScope.launch {
            val recs = repository.getRecommendations(30).filter { it.videoId.isNotEmpty() }
            if (recs.isNotEmpty()) controller.appendTracks(recs)
            else controller.setRepeatAll()
        }
    }

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
