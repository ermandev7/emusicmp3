package com.emusic.app.player

import android.app.PendingIntent
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.os.SystemClock
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
import com.emusic.app.data.repository.toAppTrack
import com.emusic.shared.radio.RadioEngine
import com.google.common.collect.ImmutableList
import com.google.common.util.concurrent.Futures
import com.google.common.util.concurrent.ListenableFuture
import com.google.common.util.concurrent.SettableFuture
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
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

    /**
     * "Modo radio": se activa cuando la cola arrancó de una búsqueda (manual, por voz o
     * Android Auto/Asistente) en vez de una sección curada (Favoritos, Historial, Para ti,
     * Playlists). La lógica vive en el módulo `shared` para que la app de iPhone use
     * exactamente la misma; acá solo queda lo que depende del reproductor.
     */
    @Inject lateinit var radio: RadioEngine

    private lateinit var player: ExoPlayer
    private lateinit var mediaSession: MediaLibrarySession
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    /** Saltos automáticos consecutivos tras error; se resetea al reproducir bien. */
    private var autoSkipCount = 0

    /**
     * Tope fijo de saltos automáticos consecutivos ante fallos de resolución. NO se
     * ata al tamaño de la cola: en modo radio la cola crece sola, así que un guard
     * relativo nunca frenaba y un corte de red transitorio hacía que el reproductor
     * saltara por toda la cola hasta quedarse en silencio (solo se arreglaba reabriendo
     * la app). Con un tope fijo, tras N fallos seguidos paramos limpio y el usuario
     * puede reintentar.
     */
    private val maxConsecutiveAutoSkips = 5

    /**
     * Si la pista actual ya se reintentó una vez tras un fallo. Un fallo de resolución
     * suele ser transitorio (timeout puntual, URL recién caducada): reintentar la misma
     * pista una vez la recupera sin perderla. Si vuelve a fallar, ahí sí saltamos.
     * Se resetea al cambiar de pista o al reproducir bien.
     */
    private var currentItemRetried = false

    /**
     * Detección de skips (el usuario pasa a la siguiente en los primeros
     * [SKIP_THRESHOLD_MS]). [startedItemId] es la pista que llegó a sonar de verdad:
     * saltar una que todavía cargaba no cuenta. [autoSkipping] marca los saltos que
     * hace el propio servicio tras un error, que tampoco son del usuario.
     */
    private var startedItemId: String? = null
    private var autoSkipping = false

    /**
     * Quien pulsa siguiente varias veces seguidas buscando una canción no está
     * diciendo "no me gusta" de cada una (ver [SkipFilter]). El skip se confirma
     * cuando la canción siguiente lleva [SKIP_CONFIRM_PLAY_MS] sonando.
     */
    private val skipFilter = SkipFilter<MediaItem>(BURST_GAP_MS) { SystemClock.elapsedRealtime() }
    private var skipConfirmJob: Job? = null

    /**
     * Listas cacheadas de cada sección de Android Auto (browse + reproducción). Al
     * tocar un item buscamos en estas cachés para reproducir la lista completa.
     */
    private var cachedRecommendations: List<Track> = emptyList()
    private var cachedFavorites: List<Track> = emptyList()
    private var cachedHistory: List<Track> = emptyList()

    /**
     * Última búsqueda por voz/texto desde Android Auto (`onSearch`). Se guarda para
     * que `onGetSearchResult` devuelva la lista y para que, al tocar un resultado,
     * `onAddMediaItems` arranque en modo radio desde esa canción.
     */
    private var cachedSearch: List<Track> = emptyList()
    private var cachedSearchQuery: String = ""

    companion object {
        private const val TAG = "MusicService"
        const val ROOT_ID = "ROOT"
        const val QUEUE_ID = "QUEUE"
        const val RECOMMENDED_ID = "RECOMMENDED"
        const val FAVORITES_ID = "FAVORITES"
        const val HISTORY_ID = "HISTORY"
        const val SCHEME = "emusic"

        /** "Para ti": nº de más escuchadas al frente y tamaño total de la lista. */
        private const val PARA_TI_MOST_PLAYED = 15
        private const val PARA_TI_TOTAL = 30

        /** Resultados máximos que se muestran en una búsqueda de Android Auto. */
        private const val SEARCH_RESULTS_LIMIT = 20

        /** Pasar a la siguiente antes de este tiempo cuenta como skip ("no me gusta"). */
        private const val SKIP_THRESHOLD_MS = 30_000L

        /** Un skip se confirma cuando la canción siguiente lleva este tiempo sonando. */
        private const val SKIP_CONFIRM_PLAY_MS = 8_000L

        /** Dos cambios de pista más juntos que esto = el usuario está buscando. */
        private const val BURST_GAP_MS = 12_000L

        /** Marca en RequestMetadata.extras: esta cola arranca "modo radio" (ver [LibrarySessionCallback]). */
        const val EXTRA_RADIO_SEED = "com.emusic.app.RADIO_SEED"

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
                if (autoSkipCount >= maxConsecutiveAutoSkips) {
                    // Tope alcanzado: probablemente un corte de red/resolución. Paramos
                    // limpio en vez de arrasar la cola (y de que el modo radio la siga
                    // alimentando con pistas que también fallarán). El usuario reintenta.
                    Log.e(TAG, "PlayerError code=${error.errorCodeName}; $autoSkipCount saltos seguidos, me detengo")
                    player.pause()
                    autoSkipCount = 0
                    return
                }
                // Reintento transitorio: una vez por pista, sin saltar ni contar skip.
                if (!currentItemRetried) {
                    currentItemRetried = true
                    Log.w(TAG, "PlayerError code=${error.errorCodeName}; reintento misma pista")
                    player.prepare()
                    player.play()
                    return
                }
                Log.e(TAG, "PlayerError code=${error.errorCodeName}; salto ${autoSkipCount + 1}/$maxConsecutiveAutoSkips")
                if (player.hasNextMediaItem()) {
                    autoSkipCount++
                    autoSkipping = true // no es un skip del usuario
                    player.seekToNext()
                    player.prepare()
                    player.play()
                }
            }

            override fun onPlaybackStateChanged(playbackState: Int) {
                // Un track que llega a READY resetea el contador de saltos y el reintento.
                if (playbackState == Player.STATE_READY) {
                    autoSkipCount = 0
                    currentItemRetried = false
                }
            }

            // Modo radio: cada vez que cambia la canción (avance natural o salto
            // manual de siguiente/anterior), si queda poca cola, pedimos más
            // recomendadas para que nunca se corte la música.
            override fun onMediaItemTransition(mediaItem: MediaItem?, reason: Int) {
                // Pista nueva: el reintento vuelve a estar disponible.
                currentItemRetried = false
                maybeExtendRadio()
            }

            override fun onIsPlayingChanged(isPlaying: Boolean) {
                if (isPlaying) {
                    startedItemId = player.currentMediaItem?.mediaId
                    // Hay un skip pendiente: se confirma si esta canción suena un rato
                    // sin que el usuario vuelva a cambiar (no estaba buscando).
                    if (skipFilter.pending != null) {
                        skipConfirmJob?.cancel()
                        skipConfirmJob = serviceScope.launch {
                            delay(SKIP_CONFIRM_PLAY_MS)
                            skipFilter.confirm()?.let { reportSkip(it) }
                        }
                    }
                } else {
                    // Pausa o carga: el tiempo de confirmación cuenta solo sonando.
                    skipConfirmJob?.cancel()
                }
            }

            // Cambio de pista hecho por el usuario (botón, notificación, Android Auto o
            // voz). Candidato a skip: "siguiente" en los primeros 30 s de una canción
            // que ya sonaba. "Anterior" no es rechazo (cancela el pendiente: vuelve a
            // ella) y elegir una canción concreta de la cola tampoco ("quiero esa").
            override fun onPositionDiscontinuity(
                oldPosition: Player.PositionInfo,
                newPosition: Player.PositionInfo,
                reason: Int
            ) {
                if (reason != Player.DISCONTINUITY_REASON_SEEK) return
                if (autoSkipping) {
                    autoSkipping = false
                    return
                }
                val oldIndex = oldPosition.mediaItemIndex
                val newIndex = newPosition.mediaItemIndex
                if (newIndex == oldIndex) return
                val timeline = player.currentTimeline
                val nextIndex = if (oldIndex < timeline.windowCount) {
                    timeline.getNextWindowIndex(oldIndex, player.repeatMode, player.shuffleModeEnabled)
                } else C.INDEX_UNSET
                val skipped = oldPosition.mediaItem
                val candidate = skipped?.takeIf {
                    newIndex == nextIndex &&
                        it.mediaId == startedItemId &&
                        oldPosition.positionMs < SKIP_THRESHOLD_MS
                }
                skipConfirmJob?.cancel()
                if (skipFilter.onUserTrackChange(candidate)) {
                    Log.d(TAG, "Skip descartado: el usuario está buscando canción")
                }
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

        // Precarga "Para ti" (más escuchadas) para que Android Auto la muestre al
        // instante en vez del skeleton: getHistory tarda 5-7s y Auto puede caducar
        // antes de recibir datos. Al tener la caché lista, onGetChildren responde ya.
        refreshMostPlayedCache()
    }

    /**
     * Refresca en background la caché de "Para ti": primero las más escuchadas
     * (señal fuerte: lo que el usuario realmente repite, incluye favoritas que ha
     * reproducido) y a continuación las recomendadas del algoritmo, sin duplicar.
     * Las más escuchadas salen de getHistory ordenado por playCount (mismo endpoint
     * que Historial, sin API nueva); las recomendadas del endpoint existente.
     */
    private fun refreshMostPlayedCache() {
        serviceScope.launch(Dispatchers.IO) {
            try {
                val mostPlayed = repository.getMostPlayed(PARA_TI_MOST_PLAYED)
                val recommended = repository.getRecommendations(PARA_TI_TOTAL)
                cachedRecommendations = (mostPlayed + recommended)
                    .distinctBy { it.videoId }
                    .take(PARA_TI_TOTAL)
            } catch (e: Exception) {
                Log.e(TAG, "No se pudo precargar Para ti", e)
            }
        }
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
     * Avisa al servidor de un skip (endpoint existente PATCH history/{id}/skip). Si la
     * canción no estaba en el historial (típico en una recomendada de la radio, que
     * nunca se registra al sonar), el servidor responde 404: se registra y se marca,
     * para que el algoritmo aprenda también de lo que se salta en la radio.
     */
    private fun reportSkip(item: MediaItem) {
        val videoId = item.mediaId
        if (videoId.isEmpty()) return
        val md = item.mediaMetadata
        Log.d(TAG, "Skip: ${md.artist} - ${md.title}")
        serviceScope.launch(Dispatchers.IO) {
            if (repository.markSkipped(videoId)) return@launch
            repository.addHistory(
                Track(
                    title = md.title?.toString().orEmpty(),
                    uploaderName = md.artist?.toString().orEmpty(),
                    thumbnail = md.artworkUri?.toString(),
                    videoIdFromJson = videoId
                )
            )
            repository.markSkipped(videoId)
        }
    }

    /** true si el item viene marcado como semilla de "modo radio" (ver [EXTRA_RADIO_SEED]). */
    private fun isRadioSeed(items: List<MediaItem>): Boolean =
        items.singleOrNull()?.requestMetadata?.extras?.getBoolean(EXTRA_RADIO_SEED, false) == true

    /** Activa el modo radio y reinicia sus contadores (nueva sesión de búsqueda). */
    private fun startRadio(seedArtist: String) {
        radio.start()
        maybeExtendRadio(seedArtist)
    }

    private fun stopRadio() {
        radio.stop()
    }

    /**
     * Si estamos en modo radio y a la cola le quedan pocas canciones, le pide la próxima
     * tanda a [RadioEngine] (módulo `shared`, mismo código que va a usar iOS) y la encola.
     * Acá solo queda lo específico de ExoPlayer: leer la cola actual y agregar los items.
     *
     * @param seedArtistOverride artista a usar en la búsqueda; si es null, se toma del
     * track que está sonando en este momento (caso: extensión disparada por
     * onMediaItemTransition, ya con el player actualizado).
     */
    private fun maybeExtendRadio(seedArtistOverride: String? = null) {
        if (!radio.shouldExtend(player.mediaItemCount - player.currentMediaItemIndex)) return
        serviceScope.launch(Dispatchers.IO) {
            try {
                val existingIds = withContext(Dispatchers.Main) {
                    (0 until player.mediaItemCount).map { player.getMediaItemAt(it).mediaId }.toSet()
                }
                val seedArtist = seedArtistOverride
                    ?: withContext(Dispatchers.Main) { player.currentMediaItem?.mediaMetadata?.artist?.toString() }
                    ?: ""

                val batch = radio.nextBatch(seedArtist, existingIds).map { it.toAppTrack() }
                if (batch.isEmpty()) {
                    Log.d(TAG, "Radio: sin candidatos nuevos (artista='$seedArtist')")
                    return@launch
                }
                val items = batch.map { it.toMediaItem(placeholderUri(it.videoId)) }
                withContext(Dispatchers.Main) { player.addMediaItems(items) }
                Log.d(TAG, "Radio: agregadas ${items.size} (artista='$seedArtist')")
            } catch (e: Exception) {
                Log.e(TAG, "Radio: fallo extendiendo cola", e)
            }
        }
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

        // Android Auto / Gemini: "pon música de X". Arranca "modo radio": solo la
        // canción pedida, y [maybeExtendRadio] va agregando recomendadas del algoritmo
        // a medida que se necesitan (en vez de encolar TODA la lista de resultados,
        // que podía traer covers o versiones de otros artistas como "siguiente").
        override fun onAddMediaItems(
            mediaSession: MediaSession,
            controller: MediaSession.ControllerInfo,
            mediaItems: List<MediaItem>
        ): ListenableFuture<List<MediaItem>> {
            val searchQuery = mediaItems.firstOrNull()?.requestMetadata?.searchQuery
            if (searchQuery.isNullOrEmpty()) {
                // Si el item tocado pertenece a una sección (Para ti / Favoritos /
                // Historial), reproducir TODA esa lista desde ahí (cola navegable,
                // sin modo radio: esas listas ya son curadas).
                val firstId = mediaItems.firstOrNull()?.mediaId
                // Resultado de búsqueda por voz/texto en Android Auto (onSearch): se
                // trata como "pon X" → modo radio desde esa canción, igual que la
                // búsqueda por voz directa. Va ANTES que las secciones curadas.
                if (firstId != null) {
                    val hit = cachedSearch.firstOrNull { it.videoId == firstId }
                    if (hit != null) {
                        startRadio(hit.displayArtist)
                        if (hit.videoId.isNotEmpty()) serviceScope.launch(Dispatchers.IO) { repository.addHistory(hit) }
                        return Futures.immediateFuture(listOf(hit.toMediaItem(placeholderUri(hit.videoId))))
                    }
                }
                if (firstId != null) {
                    val section = sequenceOf(cachedRecommendations, cachedFavorites, cachedHistory)
                        .firstOrNull { list -> list.any { it.videoId == firstId } }
                    if (section != null) {
                        stopRadio()
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
                // ¿Viene marcado como semilla de radio (tap en un resultado de búsqueda,
                // App Actions)? Si no, es una lista curada de la app (Home/Favoritos/
                // Historial/Playlists/Cola) → se respeta tal cual, sin modo radio.
                if (isRadioSeed(mediaItems)) {
                    val seedArtist = mediaItems.first().mediaMetadata.artist?.toString().orEmpty()
                    startRadio(seedArtist)
                } else {
                    stopRadio()
                }
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
                    // Solo la primera canción encontrada: el resto de la cola la arma
                    // maybeExtendRadio() con recomendadas, igual que en el resto de la app.
                    val first = tracks.first()
                    val firstUrl = runCatching { resolveStreamUrl(first.videoId) }.getOrNull()
                    val seedItem = first.toMediaItem(
                        if (!firstUrl.isNullOrEmpty()) Uri.parse(firstUrl) else placeholderUri(first.videoId)
                    )
                    Log.d(TAG, "onAddMediaItems search: 1 semilla para '$searchQuery' (modo radio)")
                    future.set(listOf(seedItem))
                    startRadio(first.displayArtist)
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
                // "Para ti" = más escuchadas (getHistory ordenado por playCount, mismo
                // endpoint que Historial, sin API nueva). Si la caché ya está lista
                // (precargada en onCreate), se devuelve al instante para evitar el
                // skeleton de Android Auto; luego se refresca en background para la
                // próxima vez. Solo si la caché está vacía (primer arranque) se hace la
                // carga asíncrona clásica.
                RECOMMENDED_ID -> {
                    val cached = cachedRecommendations
                    if (cached.isNotEmpty()) {
                        // Caché lista (más escuchadas + recomendadas): respuesta
                        // instantánea, sin skeleton. Se refresca para la próxima.
                        refreshMostPlayedCache()
                        val items = ImmutableList.copyOf(
                            cached.map { it.toMediaItem(placeholderUri(it.videoId)) }
                        )
                        Futures.immediateFuture(LibraryResult.ofItemList(items, params))
                    } else {
                        // Arranque en frío: devolvemos ya las más escuchadas (consulta
                        // rápida) para no mostrar skeleton, y en paralelo construimos la
                        // caché combinada (más escuchadas + recomendadas) para la próxima.
                        refreshMostPlayedCache()
                        loadSection(params, "Para ti") {
                            repository.getMostPlayed(PARA_TI_TOTAL).also { cachedRecommendations = it }
                        }
                    }
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

        // Android Auto / Asistente: búsqueda por voz o teclado ("busca X", "pon X en
        // eMusic" cuando el asistente usa el flujo search → getSearchResult). Busca en
        // el endpoint existente, guarda los resultados y avisa al navegador.
        override fun onSearch(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            query: String,
            params: LibraryParams?
        ): ListenableFuture<LibraryResult<Void>> {
            Log.d(TAG, "onSearch: '$query'")
            serviceScope.launch(Dispatchers.IO) {
                val results = try {
                    repository.search(query).take(SEARCH_RESULTS_LIMIT)
                } catch (e: Exception) {
                    Log.e(TAG, "onSearch falló", e)
                    emptyList()
                }
                withContext(Dispatchers.Main) {
                    cachedSearch = results
                    cachedSearchQuery = query
                    session.notifySearchResultChanged(browser, query, results.size, params)
                }
            }
            return Futures.immediateFuture(LibraryResult.ofVoid(params))
        }

        override fun onGetSearchResult(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            query: String,
            page: Int,
            pageSize: Int,
            params: LibraryParams?
        ): ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> {
            // Si por timing piden otra query distinta a la cacheada, no devolvemos
            // resultados viejos de otra búsqueda.
            val source = if (query == cachedSearchQuery) cachedSearch else emptyList()
            val from = (page * pageSize).coerceAtLeast(0)
            val pageItems = source.drop(from).take(if (pageSize > 0) pageSize else source.size)
            val items = ImmutableList.copyOf(
                pageItems.map { it.toMediaItem(placeholderUri(it.videoId)) }
            )
            return Futures.immediateFuture(LibraryResult.ofItemList(items, params))
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
