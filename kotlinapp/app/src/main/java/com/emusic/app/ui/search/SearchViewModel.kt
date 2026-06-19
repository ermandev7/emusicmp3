package com.emusic.app.ui.search

import android.content.Context
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.emusic.app.data.api.Track
import com.emusic.app.data.repository.MusicRepository
import com.emusic.app.ui.setup.dataStore
import dagger.hilt.android.lifecycle.HiltViewModel
import dagger.hilt.android.qualifiers.ApplicationContext
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.stateIn
import kotlinx.coroutines.launch
import javax.inject.Inject

data class SearchUiState(
    val query: String = "",
    val results: List<Track> = emptyList(),
    val isLoading: Boolean = false,
    val error: String? = null,
    val hasSearched: Boolean = false
)

private val RECENT_SEARCHES_KEY = stringPreferencesKey("recent_searches")
private const val MAX_RECENT = 10

@HiltViewModel
class SearchViewModel @Inject constructor(
    private val repository: MusicRepository,
    @ApplicationContext private val context: Context
) : ViewModel() {

    private val _state = MutableStateFlow(SearchUiState())
    val state: StateFlow<SearchUiState> = _state.asStateFlow()

    /** Búsquedas recientes, persistidas en DataStore. */
    val recentSearches: StateFlow<List<String>> = context.dataStore.data
        .map { prefs -> prefs[RECENT_SEARCHES_KEY].toRecentList() }
        .stateIn(viewModelScope, SharingStarted.Eagerly, emptyList())

    private var searchJob: Job? = null

    fun onQueryChange(query: String) {
        _state.value = _state.value.copy(query = query)
    }

    fun search() {
        val query = _state.value.query.trim()
        if (query.isEmpty()) return

        searchJob?.cancel()
        searchJob = viewModelScope.launch {
            _state.value = _state.value.copy(isLoading = true, error = null, hasSearched = true)
            saveRecent(query)
            val results = repository.search(query)
            _state.value = _state.value.copy(results = results, isLoading = false)

            // Pre-fetch de los primeros 3 resultados
            val ids = results.take(3).map { it.videoId }
            if (ids.isNotEmpty()) repository.prefetch(ids)
        }
    }

    private suspend fun saveRecent(query: String) {
        context.dataStore.edit { prefs ->
            val current = prefs[RECENT_SEARCHES_KEY].toRecentList()
            val updated = (listOf(query) + current.filterNot { it.equals(query, ignoreCase = true) })
                .take(MAX_RECENT)
            prefs[RECENT_SEARCHES_KEY] = updated.joinToString("\n")
        }
    }

    fun removeRecent(query: String) {
        viewModelScope.launch {
            context.dataStore.edit { prefs ->
                val updated = prefs[RECENT_SEARCHES_KEY].toRecentList()
                    .filterNot { it.equals(query, ignoreCase = true) }
                prefs[RECENT_SEARCHES_KEY] = updated.joinToString("\n")
            }
        }
    }

    fun searchQuery(query: String) {
        _state.value = _state.value.copy(query = query)
        search()
    }

    fun clear() {
        searchJob?.cancel()
        _state.value = SearchUiState()
    }
}

private fun String?.toRecentList(): List<String> =
    this?.split("\n")?.map { it.trim() }?.filter { it.isNotEmpty() } ?: emptyList()
