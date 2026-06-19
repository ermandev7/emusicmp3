package com.emusic.app.ui.playlist

import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.emusic.app.ui.components.TrackItem
import com.emusic.app.ui.player.PlayerViewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PlaylistDetailScreen(
    playlistId: Int,
    playlistName: String,
    onTrackClick: () -> Unit,
    onBack: () -> Unit,
    viewModel: PlaylistDetailViewModel = hiltViewModel(),
    playerViewModel: PlayerViewModel = hiltViewModel()
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val playerState by playerViewModel.state.collectAsStateWithLifecycle()

    LaunchedEffect(playlistId) { viewModel.load(playlistId) }

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text(playlistName) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(Icons.AutoMirrored.Filled.ArrowBack, "Atrás")
                    }
                }
            )
        }
    ) { padding ->
        when {
            state.isLoading -> Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
                CircularProgressIndicator()
            }
            state.playlist?.tracks?.isEmpty() == true -> Box(
                Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center
            ) {
                Text("Esta playlist está vacía", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
            else -> {
                val tracks = state.playlist?.tracks ?: emptyList()
                LazyColumn(modifier = Modifier.padding(padding)) {
                    items(tracks) { track ->
                        TrackItem(
                            track = track,
                            isPlaying = playerState.currentTrack?.videoId == track.videoId && playerState.isPlaying,
                            onClick = {
                                playerViewModel.playTrack(track, tracks)
                                onTrackClick()
                            },
                            onMenuClick = { viewModel.removeTrack(playlistId, track.videoId) }
                        )
                    }
                    item { Spacer(Modifier.height(80.dp)) }
                }
            }
        }
    }
}
