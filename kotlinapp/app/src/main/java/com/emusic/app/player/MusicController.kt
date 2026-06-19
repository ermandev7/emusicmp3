package com.emusic.app.player

import android.content.ComponentName
import android.content.Context
import android.util.Log
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import com.emusic.app.data.api.Track
import com.emusic.app.data.download.DownloadedTrack
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.MoreExecutors
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject
import javax.inject.Singleton

data class PlayerState(
    val currentTrack: Track? = null,
    val isPlaying: Boolean = false,
    val isBuffering: Boolean = false,
    val positionMs: Long = 0L,
    val durationMs: Long = 0L,
    val queue: List<Track> = emptyList(),
    val currentIndex: Int = 0,
    val shuffleEnabled: Boolean = false,
    val repeatMode: Int = Player.REPEAT_MODE_OFF,
    /** Mensaje de error de reproducción (stream no resuelto, sin red, etc.). */
    val error: String? = null
)

@Singleton
class MusicController @Inject constructor(
    @ApplicationContext private val context: Context
) {
    private companion object { const val TAG = "MusicController" }

    private val _state = MutableStateFlow(PlayerState())
    val state: StateFlow<PlayerState> = _state.asStateFlow()

    // Track que se pidió reproducir pero aún no suena (URL resolviéndose, 5-7s).
    // Vive en el singleton para que TODAS las pantallas (búsqueda, reproductor)
    // compartan el mismo indicador de carga → el skeleton se muestra siempre.
    private val _loadingTrack = MutableStateFlow<Track?>(null)
    val loadingTrack: StateFlow<Track?> = _loadingTrack.asStateFlow()

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private var loadTimeoutJob: Job? = null

    /** true cuando se reproducen descargas locales (carga instantánea, sin skeleton). */
    private var localPlayback = false

    private var controllerFuture: ListenableFuture<MediaController>? = null
    private var controller: MediaController? = null

    private val listener = object : Player.Listener {
        override fun onIsPlayingChanged(isPlaying: Boolean) {
            // Si empieza a sonar, ya no hay error.
            _state.value = _state.value.copy(
                isPlaying = isPlaying,
                error = if (isPlaying) null else _state.value.error
            )
            maybeClearLoading()
        }

        override fun onPlaybackStateChanged(playbackState: Int) {
            _state.value = _state.value.copy(
                isBuffering = playbackState == Player.STATE_BUFFERING,
                error = if (playbackState == Player.STATE_READY) null else _state.value.error
            )
            maybeClearLoading()
        }

        override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
            // Empieza una pista nueva (incluido el auto-salto tras un fallo): limpiar el
            // error. El buffering se deriva del estado REAL del player (no se fuerza a
            // true) para no dejar el spinner pegado en transiciones gapless ya cargadas.
            _state.value = _state.value.copy(
                error = null,
                isBuffering = controller?.playbackState == Player.STATE_BUFFERING
            )
            updateCurrentTrackFromMediaItem(mediaItem)
            maybeClearLoading()
        }

        override fun onPlayerError(error: PlaybackException) {
            Log.e(TAG, "PlayerError code=${error.errorCode} name=${error.errorCodeName}", error)
            clearLoadingTrack()
            _state.value = _state.value.copy(
                isBuffering = false,
                error = "No se pudo reproducir"
            )
        }

        override fun onShuffleModeEnabledChanged(shuffleModeEnabled: Boolean) {
            _state.value = _state.value.copy(shuffleEnabled = shuffleModeEnabled)
        }

        override fun onRepeatModeChanged(repeatMode: Int) {
            _state.value = _state.value.copy(repeatMode = repeatMode)
        }
    }

    fun connect() {
        val sessionToken = SessionToken(
            context,
            ComponentName(context, MusicService::class.java)
        )
        controllerFuture = MediaController.Builder(context, sessionToken).buildAsync()
        controllerFuture?.addListener({
            controller = controllerFuture?.get()
            controller?.addListener(listener)
            updateCurrentTrackFromMediaItem(controller?.currentMediaItem)
        }, MoreExecutors.directExecutor())
    }

    fun disconnect() {
        controller?.removeListener(listener)
        controllerFuture?.let { MediaController.releaseFuture(it) }
        controller = null
    }

    /** true si el MediaController ya está conectado al servicio. */
    fun isConnected(): Boolean = controller != null

    /**
     * Carga la cola completa en ExoPlayer y empieza a reproducir [track].
     * Todos los items usan una URI placeholder que el servicio resuelve bajo
     * demanda, así que siguiente/anterior funcionan de forma nativa.
     */
    fun playTrack(track: Track, queue: List<Track> = emptyList()) {
        val ctrl = controller ?: return
        val q = queue.ifEmpty { listOf(track) }
        val index = q.indexOfFirst { it.videoId == track.videoId }.coerceAtLeast(0)
        // Actualizar estado ANTES de setMediaItems para que onMediaItemTransition
        // pueda resolver el track completo desde la cola.
        _state.value = _state.value.copy(
            currentTrack = track,
            queue = q,
            currentIndex = index,
            isBuffering = true,
            error = null
        )
        // Indicador de carga compartido → skeleton en el reproductor mientras resuelve.
        localPlayback = false
        _loadingTrack.value = track
        loadTimeoutJob?.cancel()
        loadTimeoutJob = scope.launch {
            delay(30_000L)
            if (_loadingTrack.value?.videoId == track.videoId) _loadingTrack.value = null
        }
        ctrl.setMediaItems(q.map { it.toMediaItem() }, index, 0L)
        ctrl.prepare()
        ctrl.play()
    }

    private fun clearLoadingTrack() {
        loadTimeoutJob?.cancel()
        if (_loadingTrack.value != null) _loadingTrack.value = null
    }

    /** Oculta el skeleton/spinner en cuanto la reproducción arranca de verdad. */
    private fun maybeClearLoading() {
        val loading = _loadingTrack.value ?: return
        val ctrl = controller ?: return
        // Si ya está sonando, no hay nada que cargar → limpiar siempre (a prueba de
        // desajustes de videoId entre la cola y el item que realmente suena).
        if (ctrl.isPlaying) { clearLoadingTrack(); return }
        // Si está en pausa pero el track pedido ya quedó listo (READY), también.
        if (_state.value.currentTrack?.videoId == loading.videoId &&
            ctrl.playbackState == Player.STATE_READY) {
            clearLoadingTrack()
        }
    }

    /**
     * Marca el track que se va a cargar (siguiente/anterior) para mostrar el skeleton
     * mientras se resuelve su URL. No aplica a reproducción local (instantánea).
     */
    private fun markLoadingForIndex(index: Int) {
        if (localPlayback || index == C.INDEX_UNSET || index < 0) return
        val track = _state.value.queue.getOrNull(index) ?: return
        _loadingTrack.value = track
        loadTimeoutJob?.cancel()
        loadTimeoutJob = scope.launch {
            delay(30_000L)
            if (_loadingTrack.value?.videoId == track.videoId) _loadingTrack.value = null
        }
    }

    /**
     * Reproduce archivos descargados localmente (content:// URIs). No pasa por la
     * API ni la red: el DefaultDataSource lee el contenido directamente.
     */
    fun playLocal(tracks: List<DownloadedTrack>, startIndex: Int) {
        val ctrl = controller ?: return
        if (tracks.isEmpty()) return
        val idx = startIndex.coerceIn(0, tracks.size - 1)
        val items = tracks.map { dt ->
            MediaItem.Builder()
                .setMediaId(dt.uri)
                .setUri(dt.uri)
                .setMediaMetadata(
                    MediaMetadata.Builder()
                        .setTitle(dt.title)
                        .setArtist(dt.artist)
                        .setArtworkUri(
                            if (dt.thumbnailUrl.isNotEmpty())
                                android.net.Uri.parse(dt.thumbnailUrl) else null
                        )
                        .setIsPlayable(true)
                        .build()
                )
                .build()
        }
        // Representar la cola como Tracks para que la UI existente muestre título,
        // artista y carátula de cada descarga.
        val asTracks = tracks.map {
            Track(
                title = it.title,
                uploaderName = it.artist,
                thumbnailUrl = it.thumbnailUrl,
                videoIdFromJson = it.uri
            )
        }
        // Reproducción local: carga al instante, sin skeleton.
        localPlayback = true
        clearLoadingTrack()
        _state.value = _state.value.copy(
            currentTrack = asTracks[idx],
            queue = asTracks,
            currentIndex = idx,
            isBuffering = true,
            error = null
        )
        ctrl.setMediaItems(items, idx, 0L)
        ctrl.prepare()
        ctrl.play()
    }

    /** Feedback inmediato mientras se busca/resuelve por voz (spinner en el botón play). */
    fun setLoading() { _state.value = _state.value.copy(isBuffering = true, error = null) }
    fun clearLoading() { _state.value = _state.value.copy(isBuffering = false) }

    /** Reintenta reproducir tras un error (re-resuelve el stream del track actual). */
    fun retry() {
        val ctrl = controller ?: return
        _state.value = _state.value.copy(error = null, isBuffering = true)
        ctrl.prepare()
        ctrl.play()
    }

    /** Salta directamente a un índice de la cola (usado por la pantalla de cola). */
    fun seekToIndex(index: Int) {
        val ctrl = controller ?: return
        if (index < 0 || index >= ctrl.mediaItemCount) return
        ctrl.seekToDefaultPosition(index)
        ctrl.play()
    }

    /** Mueve un item de la cola de [from] a [to] (reordenar arrastrando). */
    fun moveQueueItem(from: Int, to: Int) {
        val ctrl = controller ?: return
        val count = ctrl.mediaItemCount
        if (from !in 0 until count || to !in 0 until count || from == to) return
        ctrl.moveMediaItem(from, to)
        val newQueue = _state.value.queue.toMutableList().apply { add(to, removeAt(from)) }
        _state.value = _state.value.copy(
            queue = newQueue,
            currentIndex = ctrl.currentMediaItemIndex
        )
    }

    /** Quita un item de la cola. */
    fun removeQueueItem(index: Int) {
        val ctrl = controller ?: return
        if (index < 0 || index >= ctrl.mediaItemCount) return
        ctrl.removeMediaItem(index)
        val newQueue = _state.value.queue.toMutableList().apply { removeAt(index) }
        _state.value = _state.value.copy(
            queue = newQueue,
            currentIndex = ctrl.currentMediaItemIndex
        )
    }

    fun play() { controller?.play() }
    fun pause() { controller?.pause() }

    fun seekToNext() {
        val ctrl = controller ?: return
        markLoadingForIndex(ctrl.nextMediaItemIndex)
        ctrl.seekToNext()
    }

    fun seekToPrevious() {
        val ctrl = controller ?: return
        // seekToPrevious reinicia la pista actual si la posición supera el umbral
        // (no cambia de track) → en ese caso no mostramos skeleton.
        val willChangeTrack = ctrl.currentPosition <= ctrl.maxSeekToPreviousPosition
        if (willChangeTrack) markLoadingForIndex(ctrl.previousMediaItemIndex)
        ctrl.seekToPrevious()
    }
    fun seekTo(positionMs: Long) { controller?.seekTo(positionMs) }
    fun toggleShuffle() { controller?.shuffleModeEnabled = !(controller?.shuffleModeEnabled ?: false) }
    fun cycleRepeatMode() {
        val next = when (controller?.repeatMode) {
            Player.REPEAT_MODE_OFF -> Player.REPEAT_MODE_ALL
            Player.REPEAT_MODE_ALL -> Player.REPEAT_MODE_ONE
            else -> Player.REPEAT_MODE_OFF
        }
        controller?.repeatMode = next
    }

    fun getPositionMs(): Long = controller?.currentPosition ?: 0L
    fun getDurationMs(): Long = controller?.duration?.coerceAtLeast(0L) ?: 0L

    private fun updateCurrentTrackFromMediaItem(item: MediaItem?) {
        if (item == null) return
        val index = (0 until (controller?.mediaItemCount ?: 0))
            .firstOrNull { controller?.getMediaItemAt(it)?.mediaId == item.mediaId } ?: -1
        // Preferir el track completo de la cola (tiene videoId, url, etc.)
        val fromQueue = _state.value.queue.firstOrNull { it.videoId == item.mediaId }
        if (fromQueue != null) {
            _state.value = _state.value.copy(
                currentTrack = fromQueue,
                currentIndex = if (index >= 0) index else _state.value.currentIndex
            )
            return
        }
        val meta = item.mediaMetadata
        _state.value = _state.value.copy(
            currentTrack = Track(
                title = meta.title?.toString() ?: "",
                uploaderName = meta.artist?.toString() ?: "",
                thumbnail = meta.artworkUri?.toString() ?: "",
                videoIdFromJson = item.mediaId.ifEmpty { null }
            ),
            currentIndex = if (index >= 0) index else _state.value.currentIndex
        )
    }
}

private fun Track.toMediaItem(): MediaItem =
    MediaItem.Builder()
        .setMediaId(videoId)
        .setUri(MusicService.placeholderUri(videoId))
        .setMediaMetadata(
            MediaMetadata.Builder()
                .setTitle(title)
                .setArtist(displayArtist)
                .setArtworkUri(
                    if (displayThumbnail.isNotEmpty())
                        android.net.Uri.parse(displayThumbnail)
                    else null
                )
                .setIsPlayable(true)
                .setIsBrowsable(false)
                .setMediaType(MediaMetadata.MEDIA_TYPE_MUSIC)
                .build()
        )
        .build()
