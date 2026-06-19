package com.emusic.app.ui.home

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.emusic.app.data.api.GenreStat
import com.emusic.app.data.api.Track
import com.emusic.app.data.repository.MusicRepository
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

data class HomeUiState(
    val mostPlayed: List<Track> = emptyList(),
    val recommendations: List<Track> = emptyList(),
    val topGenres: List<GenreStat> = emptyList(),
    val isLoading: Boolean = false,
    val isLoadingReco: Boolean = false,
    val genreResults: List<Track> = emptyList(),
    val loadingGenre: String? = null,
    val error: String? = null
)

@HiltViewModel
class HomeViewModel @Inject constructor(
    private val repository: MusicRepository
) : ViewModel() {

    private val _state = MutableStateFlow(HomeUiState())
    val state: StateFlow<HomeUiState> = _state.asStateFlow()

    init {
        loadAll()
    }

    fun loadAll() {
        viewModelScope.launch {
            _state.value = _state.value.copy(isLoading = true)
            val mostPlayed = repository.getMostPlayed(20)
            val genres = repository.getTopGenres()
            _state.value = _state.value.copy(
                mostPlayed = mostPlayed,
                topGenres = genres,
                isLoading = false
            )
        }
        loadRecommendations()
    }

    private fun loadRecommendations() {
        viewModelScope.launch {
            _state.value = _state.value.copy(isLoadingReco = true)
            val tracks = repository.getRecommendations()
            _state.value = _state.value.copy(recommendations = tracks, isLoadingReco = false)
        }
    }

    fun searchGenre(genreName: String, query: String, onReady: (List<Track>) -> Unit) {
        viewModelScope.launch {
            _state.value = _state.value.copy(loadingGenre = genreName)
            val results = repository.search(query)
            _state.value = _state.value.copy(genreResults = results, loadingGenre = null)
            if (results.isNotEmpty()) onReady(results)
        }
    }

    /** "No me interesa": excluye el track de futuras recomendaciones y lo quita ya. */
    fun excludeRecommendation(track: Track) {
        _state.value = _state.value.copy(
            recommendations = _state.value.recommendations.filterNot { it.videoId == track.videoId }
        )
        viewModelScope.launch {
            repository.excludeRecommendation(track)
        }
    }

    fun refresh() {
        loadAll()
    }
}
