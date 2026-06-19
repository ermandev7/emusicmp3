package com.emusic.app.player

import android.app.PendingIntent
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.util.Log
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.MediaItem
import androidx.media3.common.MediaMetadata
import androidx.media3.common.PlaybackException
import androidx.media3.common.Player
import androidx.media3.datasource.DefaultDataSource
import androidx.media3.datasource.DefaultHttpDataSource
import androidx.media3.datasource.ResolvingDataSource
import androidx.media3.exoplayer.ExoPlayer
import androidx.media3.exoplayer.source.DefaultMediaSourceFactory
import androidx.media3.session.LibraryResult
import androidx.media3.session.MediaLibraryService
import androidx.media3.session.MediaSession
import androidx.media3.session.SessionCommand
import androidx.media3.session.SessionResult
import com.emusic.app.MainActivity
import com.emusic.app.data.api.Track
import com.emusic.app.data.api.bestAudioUrl
import com.emusic.app.data.repository.MusicRepository
import com.google.common.collect.ImmutableList
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.SettableFuture
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import java.io.IOException
import javax.inject.Inject

/**
 * MediaLibraryService con Media3/ExoPlayer.
 *
 * Resolución de stream vía [ResolvingDataSource]: los MediaItems se cargan con una
 * URI placeholder `emusic://stream/<videoId>` y la URL real de audio se resuelve
 * perezosamente justo antes de abrir el stream. Ventajas:
 *  - La cola completa se carga de una vez → siguiente/anterior nativos y gapless.
 *  - Las URLs de googlevideo se re-resuelven en cada apertura → nunca caducan.
 *  - Compatible con Android Auto, Bluetooth y controles de hardware.
 */
@AndroidEntryPoint
class MusicService : MediaLibraryService() {

    @Inject lateinit var repository: MusicRepository

    private lateinit var player: ExoPlayer
    private lateinit var mediaSession: MediaLibrarySession
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    /** Saltos automáticos consecutivos tras error; se resetea al reproducir bien. */
    private var autoSkipCount = 0

    /**
     * Listas cacheadas de cada sección de Android Auto (browse + reproducción). Al
     * tocar un item buscamos en estas cachés para reproducir la lista completa.
     */
    private var cachedRecommendations: List<Track> = emptyList()
    private var cachedFavorites: List<Track> = emptyList()
    private var cachedHistory: List<Track> = emptyList()

    companion object {
        private const val TAG = "MusicService"
        const val ROOT_ID = "ROOT"
        const val QUEUE_ID = "QUEUE"
        const val RECOMMENDED_ID = "RECOMMENDED"
        const val FAVORITES_ID = "FAVORITES"
        const val HISTORY_ID = "HISTORY"
        const val SCHEME = "emusic"

        /** URI placeholder que [ResolvingDataSource] convierte en la URL real. */
        fun placeholderUri(videoId: String): Uri =
            Uri.parse("$SCHEME://stream/$videoId")
    }

    override fun onCreate() {
        super.onCreate()

        val httpFactory = DefaultHttpDataSource.Factory()
            .setUserAgent("Mozilla/5.0 (Linux; Android) eMusic/1.0")
            .setAllowCrossProtocolRedirects(true)
            .setConnectTimeoutMs(15_000)
            .setReadTimeoutMs(15_000)
        // DefaultDataSource añade soporte content:// y file:// (descargas locales),
        // delegando http/https al httpFactory.
        val upstreamFactory = DefaultDataSource.Factory(this, httpFactory)

        // Resuelve emusic://stream/<videoId> → URL de audio real bajo demanda.
        // Las URIs content:// (descargas) pasan sin tocar y se reproducen offline.
        // Cualquier fallo se convierte en IOException para que ExoPlayer lo trate
        // como error de reproducción (no como crash de la app).
        val resolvingFactory = ResolvingDataSource.Factory(upstreamFactory) { dataSpec ->
            val uri = dataSpec.uri
            if (uri.scheme == SCHEME) {
                val videoId = uri.lastPathSegment.orEmpty()
                val realUrl = try {
                    runBlocking(Dispatchers.IO) { resolveStreamUrl(videoId) }
                } catch (e: Exception) {
                    Log.e(TAG, "Error resolviendo $videoId: ${e.message}")
                    null
                }
                if (realUrl.isNullOrEmpty()) {
                    throw IOException("No se pudo resolver stream para videoId=$videoId")
                }
                dataSpec.withUri(Uri.parse(realUrl))
            } else {
                dataSpec
            }
        }

        player = ExoPlayer.Builder(this)
            .setAudioAttributes(
                AudioAttributes.Builder()
                    .setUsage(C.USAGE_MEDIA)
                    .setContentType(C.AUDIO_CONTENT_TYPE_MUSIC)
                    .build(),
                /* handleAudioFocus = */ true
            )
            .setHandleAudioBecomingNoisy(true) // pausa al desconectar auriculares
            .setMediaSourceFactory(DefaultMediaSourceFactory(resolvingFactory))
            .build()

        // Resiliencia de la cola: si un track no resuelve su URL (timeout/fallo de
        // API), saltamos al siguiente en vez de detener toda la reproducción. Así las
        // recomendaciones siguen sonando aunque alguna pista falle. Acotado al tamaño
        // de la cola para no entrar en bucle si toda la red está caída.
        player.addListener(object : Player.Listener {
            override fun onPlayerError(error: PlaybackException) {
                Log.e(TAG, "PlayerError code=${error.errorCodeName}; intentando saltar al siguiente")
                if (player.hasNextMediaItem() && autoSkipCount < player.mediaItemCount) {
                    autoSkipCount++
                    player.seekToNext()
                    player.prepare()
                    player.play()
                }
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                // Un track que llega a READY resetea el contador de saltos.
                if (playbackState == Player.STATE_READY) autoSkipCount = 0
            }
        })

        val activityIntent = PendingIntent.getActivity(
            this, 0,
            Intent(this, MainActivity::class.java).apply {
                flags = Intent.FLAG_ACTIVITY_SINGLE_TOP
            },
            PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT
        )

        mediaSession = MediaLibrarySession.Builder(this, player, LibrarySessionCallback())
            .setSessionActivity(activityIntent)
            .build()
    }

    override fun onGetSession(controllerInfo: MediaSession.ControllerInfo): MediaLibrarySession = mediaSession

    override fun onDestroy() {
        serviceScope.cancel()
        mediaSession.release()
        player.release()
        super.onDestroy()
    }

    /** Obtiene la mejor URL de audio para un videoId (o null si falla). */
    private suspend fun resolveStreamUrl(videoId: String): String? {
        if (videoId.isEmpty()) return null
        return repository.getStream(videoId)?.bestAudioUrl()
    }

    /**
     * Carga una sección de Android Auto de forma asíncrona (la API tarda 5-7s).
     * Convierte los tracks en items reproducibles con placeholder URI.
     */
    private fun loadSection(
        params: LibraryParams?,
        label: String,
        fetch: suspend () -> List<Track>
    ): ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> {
        val future = SettableFuture.create<LibraryResult<ImmutableList<MediaItem>>>()
        serviceScope.launch(Dispatchers.IO) {
            try {
                val tracks = fetch()
                val items = ImmutableList.copyOf(
                    tracks.map { it.toMediaItem(placeholderUri(it.videoId)) }
                )
                Log.d(TAG, "$label: ${items.size} items")
                future.set(LibraryResult.ofItemList(items, params))
            } catch (e: Exception) {
                Log.e(TAG, "$label falló", e)
                future.set(LibraryResult.ofItemList(ImmutableList.of(), params))
            }
        }
        return future
    }

    // ─── Android Auto: árbol de contenido + búsqueda por voz ─────────────

    inner class LibrarySessionCallback : MediaLibrarySession.Callback {

        // Requerido por MediaLibraryService: reanuda lo que esté en el player.
        override fun onPlaybackResumption(
            mediaSession: MediaSession,
            controller: MediaSession.ControllerInfo
        ): ListenableFuture<MediaSession.MediaItemsWithStartPosition> {
            val items = (0 until player.mediaItemCount).map { player.getMediaItemAt(it) }
            if (items.isEmpty()) {
                return Futures.immediateFailedFuture(
                    UnsupportedOperationException("Nada para reanudar")
                )
            }
            val startIndex = player.currentMediaItemIndex.coerceIn(0, items.size - 1)
            val startPos = player.currentPosition.coerceAtLeast(0L)
            return Futures.immediateFuture(
                MediaSession.MediaItemsWithStartPosition(
                    ImmutableList.copyOf(items), startIndex, startPos
                )
            )
        }

        // Android Auto / Gemini: "pon música de X". Devuelve la cola con placeholders.
        override fun onAddMediaItems(
            mediaSession: MediaSession,
            controller: MediaSession.ControllerInfo,
            mediaItems: List<MediaItem>
        ): ListenableFuture<List<MediaItem>> {
            val searchQuery = mediaItems.firstOrNull()?.requestMetadata?.searchQuery
            if (searchQuery.isNullOrEmpty()) {
                // Si el item tocado pertenece a una sección (Para ti / Favoritos /
                // Historial), reproducir TODA esa lista desde ahí (cola navegable).
                val firstId = mediaItems.firstOrNull()?.mediaId
                if (firstId != null) {
                    val section = sequenceOf(cachedRecommendations, cachedFavorites, cachedHistory)
                        .firstOrNull { list -> list.any { it.videoId == firstId } }
                    if (section != null) {
                        val idx = section.indexOfFirst { it.videoId == firstId }
                        val queue = section.drop(idx)
                            .map { it.toMediaItem(placeholderUri(it.videoId)) }
                        return Futures.immediateFuture(queue)
                    }
                }
                // Items ya resueltos (ej. desde la app): solo los que tengan URI/mediaId
                // válidos para no pasarle a ExoPlayer items vacíos (causaría crash).
                val safe = mediaItems
                    .map { ensurePlayableUri(it) }
                    .filter { it.localConfiguration?.uri != null }
                return Futures.immediateFuture(safe)
            }
            Log.d(TAG, "onAddMediaItems search: '$searchQuery'")
            val future = SettableFuture.create<List<MediaItem>>()
            serviceScope.launch(Dispatchers.IO) {
                try {
                    val tracks = repository.search(searchQuery)
                    if (tracks.isEmpty()) {
                        future.set(emptyList())
                        return@launch
                    }
                    // Pre-resolver la URL real del primer track para que Android Auto
                    // arranque rápido sin bloquear el hilo de carga ~9s.
                    val first = tracks.first()
                    val firstUrl = runCatching { resolveStreamUrl(first.videoId) }.getOrNull()
                    val resolved = tracks.mapIndexed { i, t ->
                        if (i == 0 && !firstUrl.isNullOrEmpty()) {
                            t.toMediaItem(Uri.parse(firstUrl))
                        } else {
                            t.toMediaItem(placeholderUri(t.videoId))
                        }
                    }
                    Log.d(TAG, "Resueltos ${resolved.size} items para '$searchQuery'")
                    future.set(resolved)
                    if (first.videoId.isNotEmpty()) launch { repository.addHistory(first) }
                } catch (e: Exception) {
                    Log.e(TAG, "Búsqueda falló", e)
                    future.set(emptyList())
                }
            }
            return future
        }

        override fun onGetLibraryRoot(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            params: LibraryParams?
        ): ListenableFuture<LibraryResult<MediaItem>> {
            val rootItem = MediaItem.Builder()
                .setMediaId(ROOT_ID)
                .setMediaMetadata(
                    MediaMetadata.Builder()
                        .setTitle("eMusic")
                        .setIsBrowsable(true)
                        .setIsPlayable(false)
                        .setMediaType(MediaMetadata.MEDIA_TYPE_FOLDER_MIXED)
                        .build()
                )
                .build()
            return Futures.immediateFuture(LibraryResult.ofItem(rootItem, params))
        }

        override fun onGetChildren(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            parentId: String,
            page: Int,
            pageSize: Int,
            params: LibraryParams?
        ): ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> {
            return when (parentId) {
                ROOT_ID -> {
                    val children = ImmutableList.of(
                        buildBrowsableItem(RECOMMENDED_ID, "Para ti", MediaMetadata.MEDIA_TYPE_PLAYLIST),
                        buildBrowsableItem(FAVORITES_ID, "Favoritos", MediaMetadata.MEDIA_TYPE_PLAYLIST),
                        buildBrowsableItem(HISTORY_ID, "Historial", MediaMetadata.MEDIA_TYPE_PLAYLIST),
                        buildBrowsableItem(QUEUE_ID, "Cola actual", MediaMetadata.MEDIA_TYPE_PLAYLIST)
                    )
                    Futures.immediateFuture(LibraryResult.ofItemList(children, params))
                }
                // Secciones cargadas desde la API (5-7s): carga asíncrona. Cada track
                // es reproducible (placeholder URI → ResolvingDataSource lo resuelve).
                RECOMMENDED_ID -> loadSection(params, "Para ti") {
                    repository.getRecommendations(30).also { cachedRecommendations = it }
                }
                FAVORITES_ID -> loadSection(params, "Favoritos") {
                    repository.getFavorites().also { cachedFavorites = it }
                }
                HISTORY_ID -> loadSection(params, "Historial") {
                    repository.getHistory().also { cachedHistory = it }
                }
                QUEUE_ID -> {
                    val items = ImmutableList.copyOf(
                        (0 until player.mediaItemCount).map { player.getMediaItemAt(it) }
                    )
                    Futures.immediateFuture(LibraryResult.ofItemList(items, params))
                }
                else -> Futures.immediateFuture(LibraryResult.ofError(LibraryResult.RESULT_ERROR_BAD_VALUE))
            }
        }

        override fun onCustomCommand(
            session: MediaSession,
            controller: MediaSession.ControllerInfo,
            customCommand: SessionCommand,
            args: Bundle
        ): ListenableFuture<SessionResult> {
            return Futures.immediateFuture(SessionResult(SessionResult.RESULT_SUCCESS))
        }
    }

    /** Garantiza que un MediaItem tenga una URI reproducible (placeholder si falta). */
    private fun ensurePlayableUri(item: MediaItem): MediaItem {
        if (item.localConfiguration?.uri != null) return item
        val videoId = item.mediaId
        if (videoId.isEmpty()) return item
        return item.buildUpon().setUri(placeholderUri(videoId)).build()
    }

    private fun buildBrowsableItem(id: String, title: String, mediaType: Int): MediaItem =
        MediaItem.Builder()
            .setMediaId(id)
            .setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(title)
                    .setIsBrowsable(true)
                    .setIsPlayable(false)
                    .setMediaType(mediaType)
                    .build()
            )
            .build()

    private fun Track.toMediaItem(uri: Uri): MediaItem =
        MediaItem.Builder()
            .setMediaId(videoId)
            .setUri(uri)
            .setMediaMetadata(
                MediaMetadata.Builder()
                    .setTitle(title)
                    .setArtist(displayArtist)
                    .setArtworkUri(
                        if (displayThumbnail.isNotEmpty()) Uri.parse(displayThumbnail) else null
                    )
                    .setIsPlayable(true)
                    .setIsBrowsable(false)
                    .setMediaType(MediaMetadata.MEDIA_TYPE_MUSIC)
                    .build()
            )
            .build()
}
