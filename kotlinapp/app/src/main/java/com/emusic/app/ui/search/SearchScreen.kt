package com.emusic.app.ui.search

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Clear
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Mic
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.unit.dp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import com.emusic.app.ui.components.TrackItem
import com.emusic.app.ui.components.TrackListSkeleton
import com.emusic.app.ui.player.PlayerViewModel
import com.emusic.app.voice.VoiceAssistant

@Composable
fun SearchScreen(
    onTrackClick: () -> Unit,
    onBack: () -> Unit,
    viewModel: SearchViewModel = hiltViewModel(),
    playerViewModel: PlayerViewModel = hiltViewModel(),
    voiceAssistant: VoiceAssistant = hiltViewModel<SearchViewModelWithVoice>().voiceAssistant
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val playerState by playerViewModel.state.collectAsStateWithLifecycle()
    val voiceState by voiceAssistant.state.collectAsStateWithLifecycle()
    val recentSearches by viewModel.recentSearches.collectAsStateWithLifecycle()
    val focusRequester = remember { FocusRequester() }
    val focusManager = LocalFocusManager.current

    // Buscar y, además, soltar el foco del campo → oculta el teclado para ver toda la
    // lista de resultados. El teclado vuelve a salir solo al tocar el cuadro de búsqueda.
    val runSearch: () -> Unit = {
        viewModel.search()
        focusManager.clearFocus()
    }

    // Los comandos de voz los maneja MainActivity (único punto, sin carreras):
    // "reproduce X" reproduce, y los de transporte controlan la reproducción.
    val snackbarHostState = remember { SnackbarHostState() }
    LaunchedEffect(voiceState.error) {
        voiceState.error?.let { snackbarHostState.showSnackbar(it) }
    }

    Box(modifier = Modifier.fillMaxSize()) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .statusBarsPadding()
        ) {
            // Barra de búsqueda compacta
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 8.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                IconButton(onClick = onBack, modifier = Modifier.size(40.dp)) {
                    Icon(Icons.AutoMirrored.Filled.ArrowBack, "Atrás", modifier = Modifier.size(22.dp))
                }
                TextField(
                    value = state.query,
                    onValueChange = viewModel::onQueryChange,
                    placeholder = { Text("Buscar canciones...", style = MaterialTheme.typography.bodyMedium) },
                    singleLine = true,
                    keyboardOptions = KeyboardOptions(imeAction = ImeAction.Search),
                    keyboardActions = KeyboardActions(onSearch = { runSearch() }),
                    trailingIcon = {
                        if (state.query.isNotEmpty()) {
                            IconButton(onClick = { viewModel.clear() }, modifier = Modifier.size(36.dp)) {
                                Icon(Icons.Default.Clear, "Limpiar", modifier = Modifier.size(18.dp))
                            }
                        }
                    },
                    modifier = Modifier
                        .weight(1f)
                        .height(48.dp)
                        .focusRequester(focusRequester),
                    shape = RoundedCornerShape(24.dp),
                    colors = TextFieldDefaults.colors(
                        focusedContainerColor = MaterialTheme.colorScheme.surfaceVariant,
                        unfocusedContainerColor = MaterialTheme.colorScheme.surfaceVariant,
                        focusedIndicatorColor = androidx.compose.ui.graphics.Color.Transparent,
                        unfocusedIndicatorColor = androidx.compose.ui.graphics.Color.Transparent
                    ),
                    textStyle = MaterialTheme.typography.bodyMedium
                )
                IconButton(
                    onClick = {
                        focusManager.clearFocus() // oculta el teclado al usar la voz
                        if (voiceState.isListening) voiceAssistant.stopListening()
                        else voiceAssistant.startDirectSearch()
                    },
                    modifier = Modifier.size(40.dp)
                ) {
                    Icon(
                        Icons.Default.Mic, "Voz",
                        modifier = Modifier.size(22.dp),
                        tint = if (voiceState.isListening) MaterialTheme.colorScheme.primary
                               else MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
                IconButton(onClick = { runSearch() }, modifier = Modifier.size(40.dp)) {
                    Icon(Icons.Default.Search, "Buscar", modifier = Modifier.size(22.dp))
                }
            }

            // Contenido
            Box(modifier = Modifier.fillMaxSize()) {
                when {
                    state.isLoading -> {
                        TrackListSkeleton(modifier = Modifier.align(Alignment.TopCenter))
                    }
                    voiceState.isListening -> {
                        Column(
                            modifier = Modifier.align(Alignment.Center),
                            horizontalAlignment = Alignment.CenterHorizontally
                        ) {
                            CircularProgressIndicator(
                                color = MaterialTheme.colorScheme.primary,
                                modifier = Modifier.size(32.dp),
                                strokeWidth = 2.5.dp
                            )
                            Spacer(Modifier.height(16.dp))
                            Text("Escuchando...", style = MaterialTheme.typography.bodyMedium)
                            if (voiceState.recognizedText.isNotEmpty()) {
                                Spacer(Modifier.height(4.dp))
                                Text(
                                    voiceState.recognizedText,
                                    style = MaterialTheme.typography.bodySmall,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                        }
                    }
                    state.results.isEmpty() && state.hasSearched -> {
                        Text(
                            "Sin resultados para \"${state.query}\"",
                            modifier = Modifier.align(Alignment.Center),
                            style = MaterialTheme.typography.bodyMedium,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    !state.hasSearched -> {
                        if (recentSearches.isEmpty()) {
                            Column(
                                modifier = Modifier.align(Alignment.Center),
                                horizontalAlignment = Alignment.CenterHorizontally
                            ) {
                                Icon(
                                    Icons.Default.Search, null,
                                    modifier = Modifier.size(48.dp),
                                    tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.4f)
                                )
                                Spacer(Modifier.height(8.dp))
                                Text(
                                    "Escribe o usa el micrófono",
                                    style = MaterialTheme.typography.bodyMedium,
                                    color = MaterialTheme.colorScheme.onSurfaceVariant
                                )
                            }
                        } else {
                            RecentSearches(
                                recent = recentSearches,
                                onSearch = { viewModel.searchQuery(it); focusManager.clearFocus() },
                                onRemove = { viewModel.removeRecent(it) }
                            )
                        }
                    }
                    else -> {
                        LazyColumn {
                            items(state.results) { track ->
                                TrackItem(
                                    track = track,
                                    isPlaying = playerState.currentTrack?.videoId == track.videoId && playerState.isPlaying,
                                    onClick = {
                                        // radioSeed = true: solo arranca esta canción; el
                                        // "siguiente" lo arma el modo radio con recomendadas
                                        // del algoritmo, no con el resto de la lista de
                                        // búsqueda (que podía traer covers/otros artistas).
                                        playerViewModel.playTrack(track, radioSeed = true)
                                        onTrackClick()
                                    }
                                )
                            }
                            item { Spacer(Modifier.height(100.dp)) }
                        }
                    }
                }
            }
        }

        SnackbarHost(snackbarHostState, modifier = Modifier.align(Alignment.BottomCenter))
    }

    LaunchedEffect(Unit) {
        // Auto-enfocar (y mostrar teclado) solo al abrir la búsqueda por primera vez.
        // Al volver con resultados ya cargados NO se enfoca, para que el teclado no tape
        // la lista; sale solo cuando el usuario toca el cuadro de búsqueda.
        if (!state.hasSearched) focusRequester.requestFocus()
    }
}

@Composable
private fun RecentSearches(
    recent: List<String>,
    onSearch: (String) -> Unit,
    onRemove: (String) -> Unit
) {
    LazyColumn(modifier = Modifier.fillMaxSize()) {
        item {
            Text(
                "Búsquedas recientes",
                style = MaterialTheme.typography.titleSmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
                modifier = Modifier.padding(start = 16.dp, top = 12.dp, bottom = 4.dp)
            )
        }
        items(recent) { q ->
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .clickable { onSearch(q) }
                    .padding(horizontal = 16.dp, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Icon(
                    Icons.Default.History, null,
                    modifier = Modifier.size(20.dp),
                    tint = MaterialTheme.colorScheme.onSurfaceVariant
                )
                Spacer(Modifier.width(16.dp))
                Text(
                    q,
                    modifier = Modifier.weight(1f),
                    style = MaterialTheme.typography.bodyMedium,
                    maxLines = 1
                )
                IconButton(onClick = { onRemove(q) }, modifier = Modifier.size(32.dp)) {
                    Icon(
                        Icons.Default.Clear, "Quitar",
                        modifier = Modifier.size(18.dp),
                        tint = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
        }
        item { Spacer(Modifier.height(80.dp)) }
    }
}
