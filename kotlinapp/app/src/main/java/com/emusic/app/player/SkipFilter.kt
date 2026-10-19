package com.emusic.app.player

/**
 * Decide si un cambio de pista del usuario es un skip ("no me gusta") o si está
 * buscando una canción a base de pulsar siguiente varias veces.
 *
 * Un candidato a skip queda pendiente ([onUserTrackChange]) y solo se confirma
 * ([confirm]) cuando la canción siguiente lleva un rato sonando, cosa que mide quien
 * usa esta clase. Si antes de confirmar hay otro cambio de pista, o el cambio llega
 * menos de [burstGapMs] después del anterior, es una búsqueda: se descarta todo,
 * incluida la canción de la que partió, que podía gustarle.
 *
 * Sin dependencias de Android para poder probarla en la JVM ([now] inyectable).
 */
class SkipFilter<T : Any>(
    private val burstGapMs: Long,
    private val now: () -> Long
) {
    /** Skip a la espera de confirmación, o null. */
    var pending: T? = null
        private set

    private var lastChangeAt: Long? = null

    /**
     * Cada cambio de pista hecho por el usuario. [candidate] es la canción saltada si
     * cumple las condiciones de skip ("siguiente" en los primeros segundos de una que
     * ya sonaba); null en otro caso ("anterior", elegir de la cola, canción ya
     * escuchada), que no marca nada pero cancela el pendiente y cuenta como cambio.
     *
     * @return true si se ha tomado como búsqueda y se descartó un pendiente.
     */
    fun onUserTrackChange(candidate: T?): Boolean {
        val t = now()
        val quick = lastChangeAt?.let { t - it < burstGapMs } ?: false
        val browsing = pending != null || quick
        val discarded = browsing && pending != null
        lastChangeAt = t
        pending = if (browsing) null else candidate
        return discarded
    }

    /** La canción siguiente ya sonó lo suficiente: devuelve el skip confirmado, si lo hay. */
    fun confirm(): T? = pending.also { pending = null }
}
