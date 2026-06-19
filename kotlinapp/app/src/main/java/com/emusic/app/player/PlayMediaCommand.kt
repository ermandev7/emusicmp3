package com.emusic.app.player

import android.util.Log
import com.emusic.app.data.repository.MusicRepository
import kotlinx.coroutines.delay
import javax.inject.Inject
import javax.inject.Singleton

/**
 * Caso de uso de App Actions (`actions.intent.PLAY_MEDIA`): busca una consulta y
 * reproduce automáticamente el primer resultado, encolando el resto. Misma lógica
 * que usa Android Auto en [MusicService.onAddMediaItems].
 */
@Singleton
class PlayMediaCommand @Inject constructor(
    private val repository: MusicRepository,
    private val controller: MusicController
) {
    /** Devuelve true si encontró y empezó a reproducir algo. */
    suspend fun searchAndPlay(rawQuery: String, artist: String? = null): Boolean {
        val query = buildString {
            append(rawQuery.trim())
            if (!artist.isNullOrBlank() && !rawQuery.contains(artist, ignoreCase = true)) {
                append(' ').append(artist.trim())
            }
        }.trim()
        if (query.isEmpty()) return false

        val tracks = repository.search(query)
        if (tracks.isEmpty()) {
            Log.w(TAG, "PLAY_MEDIA sin resultados para '$query'")
            return false
        }

        // El MusicController se conecta de forma asíncrona en MainActivity.onCreate;
        // si el intent llegó muy pronto, esperar brevemente a que esté listo.
        if (!controller.isConnected()) {
            repeat(20) {
                if (controller.isConnected()) return@repeat
                delay(150)
            }
        }
        controller.playTrack(tracks.first(), tracks)
        Log.d(TAG, "PLAY_MEDIA reproduciendo '${tracks.first().title}' (${tracks.size} en cola)")
        return true
    }

    private companion object { const val TAG = "PlayMediaCommand" }
}
