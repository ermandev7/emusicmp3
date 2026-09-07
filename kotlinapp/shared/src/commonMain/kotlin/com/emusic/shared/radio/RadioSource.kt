package com.emusic.shared.radio

import com.emusic.shared.api.Track
import com.emusic.shared.network.EMusicNetworkClient
import com.emusic.shared.network.MusicApiClient

/**
 * De dónde saca [RadioEngine] las canciones para estirar la cola. Se separa en una
 * interfaz para poder testear la lógica de la radio sin red, y para que iOS pueda
 * enchufar la misma implementación de siempre ([NetworkRadioSource]).
 */
interface RadioSource {
    /** Más canciones del artista que está sonando (la misma búsqueda que usa el buscador). */
    suspend fun searchByArtist(artist: String): List<Track>

    /** Recomendadas del algoritmo (GET /api/recommendation, igual que "Para ti"). */
    suspend fun recommendations(limit: Int): List<Track>
}

/**
 * Implementación real contra el backend. No lanza: ante cualquier fallo devuelve lista
 * vacía, para que un problema de red no corte la reproducción.
 */
class NetworkRadioSource(
    private val network: EMusicNetworkClient,
    private val api: MusicApiClient
) : RadioSource {

    override suspend fun searchByArtist(artist: String): List<Track> =
        runCatching { network.search(artist) }.getOrDefault(emptyList())

    override suspend fun recommendations(limit: Int): List<Track> =
        runCatching { api.getRecommendations(limit).items }.getOrDefault(emptyList())
}
