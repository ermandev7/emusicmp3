package com.emusic.app.ui.library

import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.Favorite
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.QueueMusic
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import coil.compose.AsyncImage
import com.emusic.app.data.api.Playlist
import com.emusic.app.data.api.Track
import com.emusic.app.data.download.DownloadedTrack
import com.emusic.app.ui.components.NowPlayingBars
import com.emusic.app.ui.components.TrackItem
import com.emusic.app.ui.components.TrackListSkeleton
import com.emusic.app.ui.components.formatDuration
import com.emusic.app.ui.player.PlayerViewModel

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LibraryScreen(
    onTrackClick: () -> Unit,
    onPlaylistClick: (Int, String) -> Unit,
    viewModel: LibraryViewModel = hiltViewModel(),
    playerViewModel: PlayerViewModel = hiltViewModel()
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val playerState by playerViewModel.state.collectAsStateWithLifecycle()
    var showCreatePlaylist by remember { mutableStateOf(false) }
    var newPlaylistName by remember { mutableStateOf("") }

    // Al volver a Biblioteca (p.ej. tras marcar un favorito en el reproductor) refrescamos
    // la pestaña visible para que los cambios se reflejen sin reiniciar la app.
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, event ->
            if (event == Lifecycle.Event.ON_RESUME) viewModel.refreshCurrentTab()
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }

    Scaffold(
        floatingActionButton = {
            if (state.tab == LibraryTab.Playlists) {
                FloatingActionButton(onClick = { showCreatePlaylist = true }) {
                    Icon(Icons.Default.Add, "Nueva playlist")
                }
            }
        }
    ) { padding ->
        Column(modifier = Modifier.padding(padding)) {
            Text(
                "Mi Biblioteca",
                style = MaterialTheme.typography.headlineSmall,
                fontWeight = FontWeight.Bold,
                modifier = Modifier.padding(start = 20.dp, end = 20.dp, top = 4.dp, bottom = 10.dp)
            )

            // Selector compacto de pestañas (chips deslizables) en lugar de un TabRow alto.
            Row(
                modifier = Modifier
                    .horizontalScroll(rememberScrollState())
                    .padding(horizontal = 16.dp),
                horizontalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                LibraryTabChip(LibraryTab.Favorites, "Favoritos", Icons.Default.Favorite, state.tab, viewModel::setTab)
                LibraryTabChip(LibraryTab.History, "Historial", Icons.Default.History, state.tab, viewModel::setTab)
                LibraryTabChip(LibraryTab.Downloads, "Descargas", Icons.Default.Download, state.tab, viewModel::setTab)
                LibraryTabChip(LibraryTab.Playlists, "Playlists", Icons.Default.QueueMusic, state.tab, viewModel::setTab)
            }
            Spacer(Modifier.height(10.dp))

            if (state.isLoading) {
                TrackListSkeleton()
            } else {
                when (state.tab) {
                    LibraryTab.Favorites -> TrackList(
                        tracks = state.favorites,
                        currentTrack = playerState.currentTrack,
                        isPlaying = playerState.isPlaying,
                        emptyMessage = "No tienes favoritos aún",
                        onTrackClick = { track ->
                            playerViewModel.playTrack(track, state.favorites)
                            onTrackClick()
                        }
                    )
                    LibraryTab.History -> TrackList(
                        tracks = state.history,
                        currentTrack = playerState.currentTrack,
                        isPlaying = playerState.isPlaying,
                        emptyMessage = "El historial está vacío",
                        onTrackClick = { track ->
                            playerViewModel.playTrack(track, state.history)
                            onTrackClick()
                        }
                    )
                    LibraryTab.Downloads -> DownloadsList(
                        downloads = state.downloads,
                        currentUri = playerState.currentTrack?.videoId,
                        isPlaying = playerState.isPlaying,
                        onClick = { index ->
                            playerViewModel.playDownloads(state.downloads, index)
                            onTrackClick()
                        },
                        onDelete = { dt -> viewModel.deleteDownload(dt.uri) }
                    )
                    LibraryTab.Playlists -> PlaylistList(
                        playlists = state.playlists,
                        onPlaylistClick = onPlaylistClick,
                        onDeletePlaylist = { viewModel.deletePlaylist(it) }
                    )
                }
            }
        }
    }

    if (showCreatePlaylist) {
        AlertDialog(
            onDismissRequest = { showCreatePlaylist = false; newPlaylistName = "" },
            title = { Text("Nueva playlist") },
            text = {
                OutlinedTextField(
                    value = newPlaylistName,
                    onValueChange = { newPlaylistName = it },
                    label = { Text("Nombre") },
                    singleLine = true
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    if (newPlaylistName.isNotBlank()) {
                        viewModel.createPlaylist(newPlaylistName.trim())
                        showCreatePlaylist = false
                        newPlaylistName = ""
                    }
                }) { Text("Crear") }
            },
            dismissButton = {
                TextButton(onClick = { showCreatePlaylist = false; newPlaylistName = "" }) { Text("Cancelar") }
            }
        )
    }
}

@Composable
private fun LibraryTabChip(
    tab: LibraryTab,
    label: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    selected: LibraryTab,
    onSelect: (LibraryTab) -> Unit
) {
    FilterChip(
        selected = selected == tab,
        onClick = { onSelect(tab) },
        label = { Text(label) },
        leadingIcon = { Icon(icon, null, Modifier.size(18.dp)) }
    )
}

@Composable
private fun TrackList(
    tracks: List<Track>,
    currentTrack: Track?,
    isPlaying: Boolean,
    emptyMessage: String,
    onTrackClick: (Track) -> Unit
) {
    if (tracks.isEmpty()) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Text(emptyMessage, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    } else {
        LazyColumn {
            items(tracks) { track ->
                TrackItem(
                    track = track,
                    isPlaying = currentTrack?.videoId == track.videoId && isPlaying,
                    onClick = { onTrackClick(track) }
                )
            }
            item { Spacer(Modifier.height(80.dp)) }
        }
    }
}

@Composable
private fun PlaylistList(
    playlists: List<Playlist>,
    onPlaylistClick: (Int, String) -> Unit,
    onDeletePlaylist: (Int) -> Unit
) {
    if (playlists.isEmpty()) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Icon(Icons.Default.QueueMusic, null, modifier = Modifier.size(48.dp),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(8.dp))
                Text("No tienes playlists aún", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    } else {
        LazyColumn {
            items(playlists) { playlist ->
                PlaylistItem(
                    playlist = playlist,
                    onClick = { onPlaylistClick(playlist.id, playlist.name) },
                    onDelete = { onDeletePlaylist(playlist.id) }
                )
            }
            item { Spacer(Modifier.height(80.dp)) }
        }
    }
}

@Composable
private fun PlaylistItem(
    playlist: Playlist,
    onClick: () -> Unit,
    onDelete: () -> Unit
) {
    ListItem(
        headlineContent = { Text(playlist.name, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        supportingContent = { Text("${playlist.trackCount} canciones") },
        leadingContent = {
            if (playlist.coverUrl != null) {
                AsyncImage(
                    model = playlist.coverUrl,
                    contentDescription = null,
                    modifier = Modifier.size(48.dp).clip(RoundedCornerShape(4.dp)),
                    contentScale = ContentScale.Crop
                )
            } else {
                Icon(Icons.Default.MusicNote, null, modifier = Modifier.size(48.dp),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        },
        trailingContent = {
            IconButton(onClick = onDelete) {
                Icon(Icons.Default.Delete, "Eliminar", tint = MaterialTheme.colorScheme.error)
            }
        },
        modifier = Modifier.clickable(onClick = onClick)
    )
}

@Composable
private fun DownloadsList(
    downloads: List<DownloadedTrack>,
    currentUri: String?,
    isPlaying: Boolean,
    onClick: (Int) -> Unit,
    onDelete: (DownloadedTrack) -> Unit
) {
    var pendingDelete by remember { mutableStateOf<DownloadedTrack?>(null) }

    pendingDelete?.let { dt ->
        AlertDialog(
            onDismissRequest = { pendingDelete = null },
            title = { Text("Eliminar descarga") },
            text = { Text("¿Eliminar \"${dt.title}\" del dispositivo? Esta acción no se puede deshacer.") },
            confirmButton = {
                TextButton(onClick = { onDelete(dt); pendingDelete = null }) {
                    Text("Eliminar", color = MaterialTheme.colorScheme.error)
                }
            },
            dismissButton = {
                TextButton(onClick = { pendingDelete = null }) { Text("Cancelar") }
            }
        )
    }

    if (downloads.isEmpty()) {
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            Column(horizontalAlignment = Alignment.CenterHorizontally) {
                Icon(
                    Icons.Default.Download, null, modifier = Modifier.size(48.dp),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.height(8.dp))
                Text("No tienes descargas aún", color = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.height(4.dp))
                Text(
                    "Descarga canciones desde el reproductor",
                    style = MaterialTheme.typography.labelSmall,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
            }
        }
    } else {
        LazyColumn {
            itemsIndexed(downloads) { index, dt ->
                val isCurrent = currentUri == dt.uri
                ListItem(
                    headlineContent = {
                        Text(
                            dt.title, maxLines = 1, overflow = TextOverflow.Ellipsis,
                            color = if (isCurrent) MaterialTheme.colorScheme.primary
                                    else MaterialTheme.colorScheme.onSurface
                        )
                    },
                    supportingContent = {
                        if (dt.artist.isNotEmpty()) {
                            Text(dt.artist, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    },
                    leadingContent = {
                        if (dt.thumbnailUrl.isNotEmpty()) {
                            AsyncImage(
                                model = dt.thumbnailUrl,
                                contentDescription = null,
                                modifier = Modifier.size(48.dp).clip(RoundedCornerShape(4.dp)),
                                contentScale = ContentScale.Crop
                            )
                        } else {
                            Icon(
                                Icons.Default.MusicNote, null, modifier = Modifier.size(40.dp),
                                tint = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                    },
                    trailingContent = {
                        Row(verticalAlignment = Alignment.CenterVertically) {
                            if (isCurrent && isPlaying) {
                                NowPlayingBars()
                            } else if (dt.durationMs > 0) {
                                Text(
                                    formatDuration((dt.durationMs / 1000).toInt()),
                                    style = MaterialTheme.typography.labelSmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                            IconButton(onClick = { pendingDelete = dt }) {
                                Icon(
                                    Icons.Default.Delete, "Eliminar descarga",
                                    tint = MaterialTheme.colorScheme.error
                                )
                            }
                        }
                    },
                    modifier = Modifier.clickable { onClick(index) }
                )
            }
            item { Spacer(Modifier.height(80.dp)) }
        }
    }
}
