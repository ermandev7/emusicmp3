package com.emusic.shared.network

import com.emusic.shared.api.SearchResponse
import com.emusic.shared.api.StreamInfo
import com.emusic.shared.api.Track

/**
 * Fase 3: espejo de la lógica de fallback que hoy vive en
 * com.emusic.app.data.repository.MusicRepository (search/getStream). El resto de
 * MusicRepository (favoritos, historial, playlists, recomendaciones) no tiene
 * fallback en la versión Android — esos métodos se seguirán llamando directo
 * sobre [MusicApiClient] cuando se reconecte la app en la Fase 4.
 *
 * Se prueba primero el backend propio; si falla o no trae resultados, se prueban
 * las instancias públicas de Piped en orden vía [PipedFallbackClient]. Nunca
 * lanza: ante fallo total devuelve lista/valor vacío, igual que hoy.
 */
class EMusicNetworkClient(
    private val api: MusicApiClient,
    private val fallback: PipedFallbackClient
) {
    suspend fun search(query: String): List<Track> {
        try {
            val res = api.search(query)
            val items = res.items.filter { (it.type.isEmpty() || it.type == "stream") && it.title.isNotEmpty() }
            if (items.isNotEmpty()) return items
        } catch (e: Exception) {
            // se sigue con los fallbacks públicos
        }

        val fallbackRes = fallback.search(query) ?: return emptyList()
        return fallbackRes.items.filter { (it.type.isEmpty() || it.type == "stream") && it.title.isNotEmpty() }
    }

    suspend fun getStream(videoId: String): StreamInfo? {
        try {
            val res = api.getStream(videoId)
            if (res.audioStreams.isNotEmpty()) return res
        } catch (e: Exception) {
            // se sigue con los fallbacks públicos
        }

        val fallbackRes = fallback.getStream(videoId)
        return if (fallbackRes?.audioStreams?.isNotEmpty() == true) fallbackRes else null
    }
}
