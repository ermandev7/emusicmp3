package com.emusic.app.data.download

import android.content.Context
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import com.emusic.app.ui.setup.dataStore
import com.google.gson.Gson
import com.google.gson.reflect.TypeToken
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.flow.first
import javax.inject.Inject
import javax.inject.Singleton

/** Metadatos de una descarga que MediaStore no guarda de forma fiable (artista, foto). */
data class DownloadMeta(
    val title: String = "",
    val artist: String = "",
    val thumbnailUrl: String = ""
)

/**
 * Persiste (en DataStore, como JSON) los metadatos de las canciones descargadas,
 * indexados por el nombre de archivo (DISPLAY_NAME). MediaStore no conserva la
 * carátula ni siempre el artista (webm/opus), así que los guardamos aquí y la lista
 * de descargas los recupera al mostrar/reproducir.
 */
@Singleton
class DownloadMetadataStore @Inject constructor(
    @ApplicationContext private val context: Context
) {
    private val gson = Gson()
    private val key = stringPreferencesKey("download_meta")
    private val mapType = object : TypeToken<Map<String, DownloadMeta>>() {}.type

    suspend fun getAll(): Map<String, DownloadMeta> {
        val json = context.dataStore.data.first()[key] ?: return emptyMap()
        return try { gson.fromJson(json, mapType) ?: emptyMap() } catch (_: Exception) { emptyMap() }
    }

    suspend fun save(displayName: String, meta: DownloadMeta) {
        val current = getAll().toMutableMap()
        current[displayName] = meta
        context.dataStore.edit { it[key] = gson.toJson(current, mapType) }
    }

    suspend fun remove(displayName: String) {
        val current = getAll().toMutableMap()
        if (current.remove(displayName) != null) {
            context.dataStore.edit { it[key] = gson.toJson(current, mapType) }
        }
    }
}
