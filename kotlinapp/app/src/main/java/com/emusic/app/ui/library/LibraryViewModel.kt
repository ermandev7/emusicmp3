package com.emusic.app.ui.library

import androidx.lifecycle.ViewModel
import androidx.lifecycle.viewModelScope
import com.emusic.app.data.api.GenreStat
import com.emusic.app.data.api.Playlist
import com.emusic.app.data.api.Track
import android.content.IntentSender
import com.emusic.app.data.download.DeleteOutcome
import com.emusic.app.data.download.DownloadedTrack
import com.emusic.app.data.download.DownloadsRepository
import com.emusic.app.data.repository.MusicRepository
import dagger.hilt.android.lifecycle.HiltViewModel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import javax.inject.Inject

enum class LibraryTab { Favorites, History, Downloads, Playlists }

data class LibraryUiState(
    val tab: LibraryTab = LibraryTab.Favorites,
    val favorites: List<Track> = emptyList(),
    val history: List<Track> = emptyList(),
    val downloads: List<DownloadedTrack> = emptyList(),
    val playlists: List<Playlist> = emptyList(),
    val topGenres: List<GenreStat> = emptyList(),
    val isLoading: Boolean = false
)

@HiltViewModel
class LibraryViewModel @Inject constructor(
    private val repository: MusicRepository,
    private val downloadsRepository: DownloadsRepository
) : ViewModel() {

    private val _state = MutableStateFlow(LibraryUiState())
    val state: StateFlow<LibraryUiState> = _state.asStateFlow()

    // Para borrar archivos no creados por esta instalación, el sistema exige confirmación:
    // emitimos el IntentSender y la pantalla lo lanza con el launcher de la Activity.
    private val _deleteConsent = MutableSharedFlow<IntentSender>(extraBufferCapacity = 1)
    val deleteConsent: SharedFlow<IntentSender> = _deleteConsent.asSharedFlow()
    private var pendingDeleteUri: String? = null

    init { loadAll() }

    fun setTab(tab: LibraryTab) {
        _state.value = _state.value.copy(tab = tab)
        refreshCurrentTab(tab)
    }

    /** Refresca la pestaña visible. Útil al volver del reproductor (p.ej. tras marcar un favorito). */
    fun refreshCurrentTab(tab: LibraryTab = _state.value.tab) {
        when (tab) {
            LibraryTab.Favorites -> refreshFavorites()
            LibraryTab.History -> refreshHistory()
            LibraryTab.Downloads -> refreshDownloads()
            LibraryTab.Playlists -> Unit
        }
    }

    fun refreshFavorites() {
        viewModelScope.launch {
            _state.value = _state.value.copy(favorites = repository.getFavorites())
        }
    }

    fun refreshHistory() {
        viewModelScope.launch {
            _state.value = _state.value.copy(history = repository.getHistory())
        }
    }

    fun refreshDownloads() {
        viewModelScope.launch {
            _state.value = _state.value.copy(downloads = downloadsRepository.getDownloads())
        }
    }

    fun deleteDownload(uri: String) {
        viewModelScope.launch {
            when (val outcome = downloadsRepository.deleteDownload(uri)) {
                is DeleteOutcome.NeedsConsent -> {
                    pendingDeleteUri = uri
                    _deleteConsent.emit(outcome.intentSender)
                }
                else -> _state.value = _state.value.copy(downloads = downloadsRepository.getDownloads())
            }
        }
    }

    /** Resultado del diálogo de confirmación del sistema (flujo NeedsConsent). */
    fun onDeleteConsentResult(granted: Boolean) {
        viewModelScope.launch {
            val uri = pendingDeleteUri
            pendingDeleteUri = null
            if (granted && uri != null) downloadsRepository.confirmDeleted(uri)
            _state.value = _state.value.copy(downloads = downloadsRepository.getDownloads())
        }
    }

    fun loadAll() {
        viewModelScope.launch {
            _state.value = _state.value.copy(isLoading = true)
            val favs = repository.getFavorites()
            val hist = repository.getHistory()
            val lists = repository.getPlaylists()
            val genres = repository.getTopGenres()
            val dls = downloadsRepository.getDownloads()
            _state.value = _state.value.copy(
                favorites = favs,
                history = hist,
                playlists = lists,
                topGenres = genres,
                downloads = dls,
                isLoading = false
            )
        }
    }

    fun toggleFavorite(track: Track) {
        viewModelScope.launch {
            val isFav = repository.isFavorite(track.videoId)
            if (isFav) repository.removeFavorite(track.videoId)
            else repository.addFavorite(track)
            val favs = repository.getFavorites()
            _state.value = _state.value.copy(favorites = favs)
        }
    }

    fun createPlaylist(name: String) {
        viewModelScope.launch {
            repository.createPlaylist(name)
            val lists = repository.getPlaylists()
            _state.value = _state.value.copy(playlists = lists)
        }
    }

    fun deletePlaylist(id: Int) {
        viewModelScope.launch {
            repository.deletePlaylist(id)
            val lists = repository.getPlaylists()
            _state.value = _state.value.copy(playlists = lists)
        }
    }

    fun addToPlaylist(playlistId: Int, track: Track) {
        viewModelScope.launch {
            repository.addTrackToPlaylist(playlistId, track)
            val lists = repository.getPlaylists()
            _state.value = _state.value.copy(playlists = lists)
        }
    }
}
