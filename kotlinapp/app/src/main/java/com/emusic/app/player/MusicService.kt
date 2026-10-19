package com.emusic.app.player

import android.app.PendingIntent
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.os.SystemClock
import android.util.Log
import androidx.media3.common.AudioAttributes
import androidx.media3.common.C
import androidx.media3.common.ForwardingPlayer
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
import com.google.common.util.concurrent.MoreExecutors
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
import org.json.JSONArray
import org.json.JSONObject
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
    private lateinit var diag: AutoDiagLog
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
     * Momento (elapsedRealtime) en que el usuario pulsó siguiente sin más canciones en
     * cola; 0 = nada pendiente. Se salta con la primera ampliación de la radio que
     * llegue antes de [SKIP_AFTER_EXTEND_TIMEOUT_MS] (puede ser una que ya estaba en
     * marcha, así que una petición que vuelva vacía no lo anula).
     */
    private var skipRequestedAt = 0L

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

    /** Ver refreshMostPlayedCache: aviso a Android Auto una vez por arranque del servicio. */
    private var paraTiNotifiedSinceCreate = false
    private var cachedSearchQuery: String = ""

    companion object {
        private const val TAG = "MusicService"
        const val ROOT_ID = "ROOT"
        const val QUEUE_ID = "QUEUE"
        /** Raíz que pide Android Auto para la tarjeta de sugerencias de su pantalla de inicio. */
        const val SUGGESTED_ID = "SUGGESTED"
        /** Raíz que pide el sistema para reanudar lo último ("recientes"). */
        const val RECENT_ID = "RECENT"
        const val RECOMMENDED_ID = "RECOMMENDED"
        const val FAVORITES_ID = "FAVORITES"
        const val HISTORY_ID = "HISTORY"
        const val SCHEME = "emusic"

        /** "Para ti": nº de más escuchadas al frente y tamaño total de la lista. */
        private const val PARA_TI_MOST_PLAYED = 15
        private const val PARA_TI_TOTAL = 30

        /** Resultados máximos que se muestran en una búsqueda de Android Auto. */
        private const val SEARCH_RESULTS_LIMIT = 20

        /** Caché en disco de la lista "Para ti" (ver saveParaTiToDisk). */
        private const val PREFS_AUTO_CACHE = "android_auto_cache"
        private const val KEY_PARA_TI = "para_ti"

        /** Pasar a la siguiente antes de este tiempo cuenta como skip ("no me gusta"). */
        private const val SKIP_THRESHOLD_MS = 30_000L

        /** "Siguiente" con la cola agotada: si la radio tarda más que esto, no se salta. */
        private const val SKIP_AFTER_EXTEND_TIMEOUT_MS = 20_000L

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
        diag = AutoDiagLog(this)

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

        mediaSession = MediaLibrarySession.Builder(this, AlwaysSkippablePlayer(player), LibrarySessionCallback())
            .setSessionActivity(activityIntent)
            .build()

        // Precarga "Para ti" (más escuchadas) para que Android Auto la muestre al
        // instante en vez del skeleton: getHistory tarda 5-7s y Auto puede caducar
        // antes de recibir datos. Al tener la caché lista, onGetChildren responde ya.
        cachedRecommendations = loadParaTiFromDisk()
        diag.log("Servicio creado. Para ti en disco: ${cachedRecommendations.size}")
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
            val start = SystemClock.elapsedRealtime()
            try {
                val mostPlayed = repository.getMostPlayed(PARA_TI_MOST_PLAYED)
                val recommended = repository.getRecommendations(PARA_TI_TOTAL)
                val combined = (mostPlayed + recommended)
                    .distinctBy { it.videoId }
                    .take(PARA_TI_TOTAL)
                diag.log("Refresco Para ti: ${mostPlayed.size} más escuchadas + ${recommended.size} recomendadas " +
                    "-> ${combined.size} en ${SystemClock.elapsedRealtime() - start} ms")
                if (combined.isNotEmpty()) {
                    val changed = combined != cachedRecommendations
                    cachedRecommendations = combined
                    saveParaTiToDisk(combined)
                    // Android Auto guarda en caché la tarjeta "Para ti" y no vuelve a pedirla
                    // por su cuenta: hay que avisarle. Solo si la lista cambió (si no, cada
                    // recarga de Auto dispararía otro refresco y otro aviso, en bucle), y una
                    // vez al arrancar el servicio para que coja carátulas o datos nuevos.
                    if (changed || !paraTiNotifiedSinceCreate) {
                        paraTiNotifiedSinceCreate = true
                        withContext(Dispatchers.Main) {
                            mediaSession.notifyChildrenChanged(RECOMMENDED_ID, combined.size, null)
                            mediaSession.notifyChildrenChanged(SUGGESTED_ID, combined.size, null)
                        }
                        diag.log("Avisado a Android Auto: Para ti cambió (${combined.size})")
                    }
                }
            } catch (e: Exception) {
                diag.log("Refresco Para ti FALLÓ: ${e.javaClass.simpleName}: ${e.message}")
                Log.e(TAG, "No se pudo precargar Para ti", e)
            }
        }
    }

    /**
     * La tarjeta de sugerencias de Android Auto se rinde enseguida ("Contenido no
     * disponible") y la API tarda 5-7 s, así que la última lista de "Para ti" se
     * guarda en el teléfono: al arrancar el coche se muestra al instante y luego se
     * refresca en segundo plano.
     */
    private fun saveParaTiToDisk(tracks: List<Track>) {
        val json = JSONArray()
        tracks.forEach { t ->
            json.put(
                JSONObject()
                    .put("videoId", t.videoId)
                    .put("title", t.title)
                    .put("artist", t.displayArtist)
                    .put("thumbnail", t.displayThumbnail)
                    .put("duration", t.duration)
            )
        }
        getSharedPreferences(PREFS_AUTO_CACHE, MODE_PRIVATE).edit()
            .putString(KEY_PARA_TI, json.toString())
            .apply()
    }

    private fun loadParaTiFromDisk(): List<Track> = try {
        val raw = getSharedPreferences(PREFS_AUTO_CACHE, MODE_PRIVATE).getString(KEY_PARA_TI, null)
        if (raw == null) emptyList() else {
            val json = JSONArray(raw)
            (0 until json.length()).map { i ->
                val o = json.getJSONObject(i)
                Track(
                    title = o.optString("title"),
                    uploaderName = o.optString("artist"),
                    thumbnail = o.optString("thumbnail").ifEmpty { null },
                    duration = o.optInt("duration"),
                    videoIdFromJson = o.optString("videoId")
                )
            }.filter { it.videoId.isNotEmpty() }
        }
    } catch (e: Exception) {
        Log.e(TAG, "Caché de Para ti ilegible", e)
        emptyList()
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
                withContext(Dispatchers.Main) {
                    player.addMediaItems(items)
                    // Siguiente pulsado con la cola agotada: ahora sí hay a dónde ir.
                    val requestedAt = skipRequestedAt
                    if (requestedAt != 0L) {
                        skipRequestedAt = 0L
                        val fresh = SystemClock.elapsedRealtime() - requestedAt < SKIP_AFTER_EXTEND_TIMEOUT_MS
                        if (fresh && player.hasNextMediaItem()) player.seekToNext()
                    }
                }
                Log.d(TAG, "Radio: agregadas ${items.size} (artista='$seedArtist')")
            } catch (e: Exception) {
                Log.e(TAG, "Radio: fallo extendiendo cola", e)
            }
        }
    }

    /**
     * Siguiente con la cola agotada (al arrancar por voz antes de que la radio
     * añada canciones, o al final de una lista): se pide más música al modo radio,
     * del mismo artista primero y si no recomendadas, y se salta cuando llegue.
     */
    private fun skipPastEndOfQueue() {
        skipRequestedAt = SystemClock.elapsedRealtime()
        if (!radio.isActive) radio.start()
        maybeExtendRadio()
    }

    /**
     * El player que ve la sesión (Android Auto, notificación, app). Anuncia siempre
     * "siguiente": sin canción siguiente en cola, Android Auto quitaba ⏮ ⏭ y ponía
     * ⏪10 ⏩10, y no había forma de pasar de canción hasta que la radio añadía más.
     */
    private inner class AlwaysSkippablePlayer(player: Player) : ForwardingPlayer(player) {
        override fun getAvailableCommands(): Player.Commands = withNext(super.getAvailableCommands())

        private fun withNext(commands: Player.Commands): Player.Commands =
            commands.buildUpon()
                .add(Player.COMMAND_SEEK_TO_NEXT)
                .add(Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM)
                .build()

        // ForwardingPlayer reenvía onAvailableCommandsChanged con los comandos del
        // player de verdad, no con los de getAvailableCommands(): la sesión (y Android
        // Auto) volvía a ver la cola sin "siguiente" en cuanto cambiaba algo. Se
        // envuelven los listeners para corregir también ese aviso.
        private val wrappedListeners = HashMap<Player.Listener, Player.Listener>()

        override fun addListener(listener: Player.Listener) {
            // Ojo: no usar `object : Player.Listener by listener`. Kotlin no delega los
            // métodos default de Java y se perdían todos los avisos menos este.
            val wrapped = CommandsFixingListener(listener, ::withNext)
            wrappedListeners[listener] = wrapped
            super.addListener(wrapped)
        }

        override fun removeListener(listener: Player.Listener) {
            super.removeListener(wrappedListeners.remove(listener) ?: listener)
        }

        override fun isCommandAvailable(command: Int): Boolean =
            command == Player.COMMAND_SEEK_TO_NEXT ||
                command == Player.COMMAND_SEEK_TO_NEXT_MEDIA_ITEM ||
                super.isCommandAvailable(command)

        override fun seekToNext() {
            if (hasNextMediaItem()) super.seekToNext() else skipPastEndOfQueue()
        }

        override fun seekToNextMediaItem() {
            if (hasNextMediaItem()) super.seekToNextMediaItem() else skipPastEndOfQueue()
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
                diag.log("Carga '$label' (API): ${items.size} items")
                future.set(LibraryResult.ofItemList(items, params))
            } catch (e: Exception) {
                diag.log("Carga '$label' FALLÓ: ${e.javaClass.simpleName}: ${e.message}")
                Log.e(TAG, "$label falló", e)
                future.set(LibraryResult.ofItemList(ImmutableList.of(), params))
            }
        }
        return future
    }

    // ─── Android Auto: árbol de contenido + búsqueda por voz ─────────────

    inner class LibrarySessionCallback : MediaLibrarySession.Callback {

        override fun onConnect(
            session: MediaSession,
            controller: MediaSession.ControllerInfo
        ): MediaSession.ConnectionResult {
            diag.log("Conecta ${controller.packageName} (controllerVersion=${controller.controllerVersion})")
            return super.onConnect(session, controller)
        }

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
            diag.log("addMediaItems de ${controller.packageName}: ${mediaItems.size} items, " +
                "query='${searchQuery.orEmpty()}' ids=${mediaItems.take(3).map { it.mediaId }}")
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
            // Android Auto pide raíces especiales además de la normal. Antes se
            // ignoraban y se devolvía siempre ROOT (carpetas), así que la tarjeta
            // "Para ti" de su pantalla de inicio, que necesita canciones reproducibles,
            // se quedaba cargando o en "Contenido no disponible".
            val (rootId, rootParams) = when {
                params?.isRecent == true ->
                    RECENT_ID to LibraryParams.Builder().setRecent(true).build()
                params?.isSuggested == true ->
                    SUGGESTED_ID to LibraryParams.Builder().setSuggested(true).build()
                else -> ROOT_ID to params
            }
            diag.log("getRoot de ${browser.packageName}: recent=${params?.isRecent} " +
                "suggested=${params?.isSuggested} offline=${params?.isOffline} " +
                "extras=${params?.extras?.keySet()} -> $rootId")
            val rootItem = MediaItem.Builder()
                .setMediaId(rootId)
                .setMediaMetadata(
                    MediaMetadata.Builder()
                        .setTitle("eMusic")
                        .setIsBrowsable(true)
                        .setIsPlayable(false)
                        .setMediaType(MediaMetadata.MEDIA_TYPE_FOLDER_MIXED)
                        .build()
                )
                .build()
            return Futures.immediateFuture(LibraryResult.ofItem(rootItem, rootParams))
        }

        override fun onGetChildren(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            parentId: String,
            page: Int,
            pageSize: Int,
            params: LibraryParams?
        ): ListenableFuture<LibraryResult<ImmutableList<MediaItem>>> {
            val start = SystemClock.elapsedRealtime()
            val all = childrenFor(parentId, params)
            // Antes se ignoraba la paginación y cada página devolvía la lista entera.
            return Futures.transform(all, { result ->
                val items = result.value
                val out = if (result.resultCode != LibraryResult.RESULT_SUCCESS || items == null) result
                else LibraryResult.ofItemList(paged(items, page, pageSize), result.params)
                diag.log("getChildren '$parentId' de ${browser.packageName} page=$page size=$pageSize: " +
                    "code=${out.resultCode} items=${out.value?.size} total=${items?.size} " +
                    "en ${SystemClock.elapsedRealtime() - start} ms")
                out
            }, MoreExecutors.directExecutor())
        }

        // Android Auto puede pedir un elemento suelto por su id (p. ej. una sugerencia).
        // Sin esto Media3 respondía "no soportado".
        override fun onGetItem(
            session: MediaLibrarySession,
            browser: MediaSession.ControllerInfo,
            mediaId: String
        ): ListenableFuture<LibraryResult<MediaItem>> {
            val track = sequenceOf(cachedRecommendations, cachedFavorites, cachedHistory, cachedSearch)
                .flatten().firstOrNull { it.videoId == mediaId }
            val item = track?.toMediaItem(placeholderUri(track.videoId))
                ?: (0 until player.mediaItemCount).map { player.getMediaItemAt(it) }.firstOrNull { it.mediaId == mediaId }
            diag.log("getItem '$mediaId' de ${browser.packageName}: ${if (item != null) "encontrado" else "NO encontrado"}")
            return Futures.immediateFuture(
                if (item != null) LibraryResult.ofItem(item, null)
                else LibraryResult.ofError(LibraryResult.RESULT_ERROR_BAD_VALUE)
            )
        }

        private fun <T> paged(items: List<T>, page: Int, pageSize: Int): ImmutableList<T> {
            if (pageSize <= 0 || pageSize == Int.MAX_VALUE) return ImmutableList.copyOf(items)
            val from = page.toLong() * pageSize
            if (from >= items.size) return ImmutableList.of()
            return ImmutableList.copyOf(items.subList(from.toInt(), minOf(items.size, from.toInt() + pageSize)))
        }

        private fun childrenFor(
            parentId: String,
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
                // Tarjeta de sugerencias de la pantalla de inicio de Auto: la misma
                // lista que "Para ti" (más escuchadas + recomendadas).
                RECOMMENDED_ID, SUGGESTED_ID -> {
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
                RECENT_ID -> {
                    val current = player.currentMediaItem
                    val items = if (current != null) ImmutableList.of(current) else ImmutableList.of()
                    Futures.immediateFuture(LibraryResult.ofItemList(items, params))
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
                    // content:// (ArtworkProvider): Android Auto no carga https. Carátula de
                    // YouTube por videoId (muchas miniaturas guardadas son enlaces del proxy
                    // de Piped que caducan); el provider baja la mejor resolución que exista.
                    .setArtworkUri(ArtworkProvider.contentUri(sdThumbnail))
                    .setIsPlayable(true)
                    .setIsBrowsable(false)
                    .setMediaType(MediaMetadata.MEDIA_TYPE_MUSIC)
                    .build()
            )
            .build()
}
