package com.emusic.app.ui.playlist

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.emusic.app.data.api.Playlist
import com.emusic.app.data.api.Track
import com.emusic.app.data.repository.MusicRepository
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

data class PlaylistDetailUiState(
    val playlist: Playlist? = null,
    val isLoading: Boolean = false
)

@HiltViewModel
class PlaylistDetailViewModel @Inject constructor(
    private val repository: MusicRepository
) : ViewModel() {

    private val _state = MutableStateFlow(PlaylistDetailUiState())
    val state: StateFlow<PlaylistDetailUiState> = _state.asStateFlow()

    fun load(playlistId: Int) {
        viewModelScope.launch {
            _state.value = _state.value.copy(isLoading = true)
            val playlists = repository.getPlaylists()
            val playlist = playlists.firstOrNull { it.id == playlistId }
            _state.value = _state.value.copy(playlist = playlist, isLoading = false)
        }
    }

    fun removeTrack(playlistId: Int, videoId: String) {
        viewModelScope.launch {
            repository.removeTrackFromPlaylist(playlistId, videoId)
            load(playlistId)
        }
    }
}
