package com.emusic.shared.radio

import com.emusic.shared.api.Track
import kotlinx.coroutines.sync.Mutex

/**
 * "Modo radio" compartido entre Android e iOS.
 *
 * Problema que resuelve: al tocar un resultado de búsqueda, antes se encolaba TODA la
 * lista de resultados, así que "siguiente" reproducía el resultado de al lado — que podía
 * ser un cover o el mismo tema de otro artista. Ahora se encola una sola canción (la
 * semilla) y esta clase va estirando la cola con temas coherentes a medida que se acaba.
 *
 * Prioridad al buscar candidatos:
 *  1. Más canciones del MISMO artista que está sonando (misma llamada que usa el buscador)
 *     — sigue el género/estilo real de lo que se escucha, no un perfil genérico.
 *  2. Si ese artista ya no da candidatos nuevos, las recomendadas generales del algoritmo
 *     (GET /api/recommendation, igual que "Para ti"), subiendo el `limit` en cada vuelta
 *     (20, 40, 60...) para esquivar la caché de 30 min del backend sin tocar la API.
 *
 * Esta clase no sabe nada de reproductores: no toca ExoPlayer ni AVPlayer. Cada plataforma
 * lee su propia cola, pregunta [shouldExtend] y encola lo que devuelva [nextBatch].
 */
class RadioEngine(private val source: RadioSource) {
    companion object {
        /** Límite inicial de /api/recommendation. Sube de a [LIMIT_STEP] para esquivar la caché. */
        const val BASE_LIMIT = 20
        const val LIMIT_STEP = 20
        const val MAX_ATTEMPTS = 4

        /** Se estira la cola cuando quedan estas canciones o menos (contando la actual). */
        const val LOW_QUEUE_THRESHOLD = 3

        /** Cuántas canciones se agregan por tanda. */
        const val BATCH_SIZE = 10
    }

    /** true mientras la cola actual venga de una búsqueda/voz y no de una sección curada. */
    var isActive: Boolean = false
        private set

    private var limit = BASE_LIMIT

    /** videoIds ya ofrecidos en esta sesión de radio, para no repetir. */
    private val seenIds = mutableSetOf<String>()

    /** Evita dos extensiones simultáneas (p. ej. dos transiciones de pista seguidas). */
    private val extending = Mutex()

    /** Arranca una sesión nueva de radio y reinicia contadores. */
    fun start() {
        isActive = true
        limit = BASE_LIMIT
        seenIds.clear()
    }

    /** Sale del modo radio (cola curada: Favoritos, Historial, Playlists, Para ti). */
    fun stop() {
        isActive = false
    }

    /**
     * @param remainingInQueue canciones que quedan por delante contando la actual
     * (en ExoPlayer: `mediaItemCount - currentMediaItemIndex`).
     */
    fun shouldExtend(remainingInQueue: Int): Boolean =
        isActive && remainingInQueue <= LOW_QUEUE_THRESHOLD

    /**
     * Próxima tanda de canciones para encolar. Devuelve lista vacía si el modo radio está
     * apagado, si ya hay otra extensión en curso, o si no quedan candidatos nuevos.
     *
     * @param seedArtist artista de la canción que está sonando (el "semilla" de la radio).
     * @param existingIds videoIds que ya están en la cola, para no duplicar.
     */
    suspend fun nextBatch(seedArtist: String, existingIds: Set<String>): List<Track> {
        if (!isActive) return emptyList()
        if (!extending.tryLock()) return emptyList()
        try {
            fun isNew(track: Track): Boolean =
                track.videoId.isNotEmpty() && track.videoId !in existingIds && track.videoId !in seenIds

            // 1) Mismo artista.
            var fresh: List<Track> = if (seedArtist.isNotBlank()) {
                source.searchByArtist(seedArtist).filter(::isNew)
            } else {
                emptyList()
            }

            // 2) Recomendadas del algoritmo, subiendo el límite hasta encontrar algo nuevo.
            if (fresh.isEmpty()) {
                var attempts = 0
                while (attempts < MAX_ATTEMPTS) {
                    val candidates = source.recommendations(limit)
                    fresh = candidates.filter(::isNew)
                    // Si ya salió algo nuevo, o el pool dejó de crecer con este límite
                    // (el algoritmo no tiene más para ofrecer), paramos.
                    if (fresh.isNotEmpty() || candidates.size < limit) break
                    limit += LIMIT_STEP
                    attempts++
                }
            }

            if (fresh.isEmpty()) return emptyList()

            val batch = fresh.shuffled().take(BATCH_SIZE)
            seenIds.addAll(batch.map { it.videoId })
            return batch
        } finally {
            extending.unlock()
        }
    }
}
