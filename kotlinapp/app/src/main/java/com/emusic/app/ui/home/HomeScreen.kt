package com.emusic.app.ui.home

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyRow
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.MusicNote
import androidx.compose.material.icons.filled.NotInterested
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import coil.compose.AsyncImage
import com.emusic.app.data.api.Track
import com.emusic.app.ui.components.TrackItem
import com.emusic.app.ui.components.TrackListSkeleton
import com.emusic.app.ui.player.PlayerViewModel

data class GenreButton(
    val name: String,
    val emoji: String,
    val query: String,
    val color1: Color,
    val color2: Color
)

val genres = listOf(
    GenreButton("Salsa", "💃", "salsa éxitos Héctor Lavoe Marc Anthony Rubén Blades mix", Color(0xFFE53935), Color(0xFFFF7043)),
    GenreButton("Cumbia", "🎺", "cumbia éxitos Los Ángeles Azules Grupo 5 mix bailable", Color(0xFF5E35B1), Color(0xFFAB47BC)),
    GenreButton("Rock", "🎸", "rock clásico Aerosmith Guns N Roses Metallica Mago de Oz éxitos", Color(0xFFB71C1C), Color(0xFF424242)),
    GenreButton("Reggaetón", "🔥", "reggaeton éxitos 2024 Bad Bunny Daddy Yankee Karol G mix", Color(0xFF8E24AA), Color(0xFFE040FB)),
    GenreButton("Electrónica", "🎧", "electronic dance music éxitos David Guetta Martin Garrix Avicii mix", Color(0xFF00695C), Color(0xFF26C6DA)),
    GenreButton("Reggae", "🌴", "reggae éxitos Bob Marley UB40 mix popular", Color(0xFF2E7D32), Color(0xFF66BB6A)),
    GenreButton("Bachata", "🌹", "bachata éxitos Romeo Santos Aventura Prince Royce mix", Color(0xFFD81B60), Color(0xFFF48FB1)),
    GenreButton("Pop", "⭐", "pop latino éxitos 2024 Shakira Luis Fonsi Sebastián Yatra mix", Color(0xFFFF6F00), Color(0xFFFFCA28)),
)

@Composable
fun HomeScreen(
    onTrackClick: () -> Unit,
    onSearchClick: () -> Unit,
    viewModel: HomeViewModel = hiltViewModel(),
    playerViewModel: PlayerViewModel = hiltViewModel()
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val playerState by playerViewModel.state.collectAsStateWithLifecycle()

    LazyColumn(
        modifier = Modifier.fillMaxSize().statusBarsPadding(),
        contentPadding = PaddingValues(bottom = 100.dp)
    ) {
        // Header compacto
        item {
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(horizontal = 16.dp, vertical = 12.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    "eMusic",
                    style = MaterialTheme.typography.headlineMedium.copy(fontWeight = FontWeight.Bold)
                )
                Row {
                    IconButton(onClick = { viewModel.refresh() }, modifier = Modifier.size(40.dp)) {
                        Icon(Icons.Default.Refresh, "Actualizar", modifier = Modifier.size(22.dp))
                    }
                    IconButton(onClick = onSearchClick, modifier = Modifier.size(40.dp)) {
                        Icon(Icons.Default.Search, "Buscar", modifier = Modifier.size(22.dp))
                    }
                }
            }
        }

        // Géneros
        item {
            SectionTitle("Escuchar por género")
        }
        item {
            Column(modifier = Modifier.padding(horizontal = 12.dp)) {
                genres.chunked(2).forEach { row ->
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(8.dp)
                    ) {
                        row.forEach { genre ->
                            GenreCard(
                                genre = genre,
                                isLoading = state.loadingGenre == genre.name,
                                modifier = Modifier.weight(1f),
                                onClick = {
                                    viewModel.searchGenre(genre.name, genre.query) { results ->
                                        playerViewModel.playTrack(results.first(), results)
                                        onTrackClick()
                                    }
                                }
                            )
                        }
                        if (row.size == 1) Spacer(Modifier.weight(1f))
                    }
                    Spacer(Modifier.height(8.dp))
                }
            }
        }

        if (state.isLoading) {
            item { SectionTitle("Cargando…") }
            item { TrackListSkeleton(count = 5) }
        }

        // Más escuchadas
        if (state.mostPlayed.isNotEmpty()) {
            item { SectionTitle("Más escuchadas") }
            item {
                LazyRow(contentPadding = PaddingValues(horizontal = 16.dp, vertical = 4.dp)) {
                    items(state.mostPlayed.take(10)) { track ->
                        MostPlayedCard(track = track, onClick = {
                            playerViewModel.playTrack(track, state.mostPlayed)
                            onTrackClick()
                        })
                    }
                }
            }
        }

        // Géneros favoritos
        if (state.topGenres.isNotEmpty()) {
            item { SectionTitle("Tus géneros favoritos") }
            item {
                LazyRow(contentPadding = PaddingValues(horizontal = 16.dp, vertical = 4.dp)) {
                    items(state.topGenres) { genre ->
                        GenreStatChip(genre = genre)
                    }
                }
            }
        }

        // Recomendaciones
        if (state.recommendations.isNotEmpty()) {
            item { SectionTitle("Recomendado para ti") }
            items(state.recommendations, key = { it.videoId }) { track ->
                var menuOpen by remember { mutableStateOf(false) }
                Box {
                    TrackItem(
                        track = track,
                        isPlaying = playerState.currentTrack?.videoId == track.videoId && playerState.isPlaying,
                        onClick = {
                            playerViewModel.playTrack(track, state.recommendations)
                            onTrackClick()
                        },
                        onMenuClick = { menuOpen = true }
                    )
                    DropdownMenu(expanded = menuOpen, onDismissRequest = { menuOpen = false }) {
                        DropdownMenuItem(
                            text = { Text("No me interesa") },
                            leadingIcon = { Icon(Icons.Default.NotInterested, null) },
                            onClick = {
                                menuOpen = false
                                viewModel.excludeRecommendation(track)
                            }
                        )
                    }
                }
            }
        }

        // Estado vacío
        if (!state.isLoading && !state.isLoadingReco &&
            state.mostPlayed.isEmpty() && state.recommendations.isEmpty()) {
            item {
                Box(Modifier.fillMaxWidth().padding(48.dp), contentAlignment = Alignment.Center) {
                    Column(horizontalAlignment = Alignment.CenterHorizontally) {
                        Icon(
                            Icons.Default.MusicNote, null,
                            modifier = Modifier.size(48.dp),
                            tint = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.5f)
                        )
                        Spacer(Modifier.height(12.dp))
                        Text(
                            "Escucha música para ver tus favoritas aquí",
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            style = MaterialTheme.typography.bodyMedium
                        )
                        Spacer(Modifier.height(12.dp))
                        TextButton(onClick = { viewModel.refresh() }) { Text("Reintentar") }
                    }
                }
            }
        }
    }
}

@Composable
private fun SectionTitle(text: String) {
    Text(
        text,
        style = MaterialTheme.typography.titleMedium.copy(fontWeight = FontWeight.Bold),
        modifier = Modifier.padding(horizontal = 16.dp, vertical = 12.dp)
    )
}

@Composable
private fun GenreCard(
    genre: GenreButton,
    isLoading: Boolean,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    Card(
        modifier = modifier
            .height(52.dp)
            .clickable(enabled = !isLoading, onClick = onClick),
        shape = RoundedCornerShape(10.dp),
        colors = CardDefaults.cardColors(containerColor = Color.Transparent)
    ) {
        Box(
            modifier = Modifier
                .fillMaxSize()
                .background(
                    Brush.horizontalGradient(listOf(genre.color1, genre.color2)),
                    RoundedCornerShape(10.dp)
                )
                .padding(horizontal = 12.dp),
            contentAlignment = Alignment.CenterStart
        ) {
            if (isLoading) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    CircularProgressIndicator(
                        modifier = Modifier.size(16.dp),
                        color = Color.White,
                        strokeWidth = 2.dp
                    )
                    Spacer(Modifier.width(8.dp))
                    Text("Cargando...", color = Color.White, fontSize = 12.sp)
                }
            } else {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(genre.emoji, fontSize = 18.sp)
                    Spacer(Modifier.width(8.dp))
                    Text(
                        genre.name,
                        color = Color.White,
                        fontWeight = FontWeight.SemiBold,
                        fontSize = 13.sp,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
            }
        }
    }
}

@Composable
private fun GenreStatChip(genre: com.emusic.app.data.api.GenreStat) {
    val chipColors = mapOf(
        "salsa" to Color(0xFFE53935), "bachata" to Color(0xFFD81B60),
        "reggaeton" to Color(0xFF8E24AA), "cumbia" to Color(0xFF5E35B1),
        "rock" to Color(0xFFB71C1C), "pop" to Color(0xFFFF6F00),
        "electronic" to Color(0xFF00695C), "reggae" to Color(0xFF2E7D32),
        "rap" to Color(0xFF424242), "trap" to Color(0xFF6A1B9A),
        "balada" to Color(0xFFAD1457), "ranchera" to Color(0xFF4E342E),
        "corrido" to Color(0xFF33691E), "vallenato" to Color(0xFF1E88E5),
        "merengue" to Color(0xFF3949AB), "jazz" to Color(0xFF0277BD),
        "blues" to Color(0xFF1565C0), "clasica" to Color(0xFF5D4037),
        "kpop" to Color(0xFFC2185B), "r&b" to Color(0xFF7B1FA2),
    )
    val color = chipColors[genre.genre.lowercase()] ?: MaterialTheme.colorScheme.primary
    Card(
        modifier = Modifier.padding(end = 8.dp),
        shape = RoundedCornerShape(20.dp),
        colors = CardDefaults.cardColors(containerColor = color)
    ) {
        Row(
            modifier = Modifier.padding(horizontal = 14.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            Text(
                text = genre.genre.replaceFirstChar { it.uppercase() },
                style = MaterialTheme.typography.labelMedium,
                color = Color.White
            )
            Spacer(Modifier.width(4.dp))
            Text(
                text = "${genre.count}",
                style = MaterialTheme.typography.labelSmall,
                color = Color.White.copy(alpha = 0.7f)
            )
        }
    }
}

@Composable
private fun MostPlayedCard(track: Track, onClick: () -> Unit) {
    Card(
        onClick = onClick,
        modifier = Modifier
            .width(140.dp)
            .padding(end = 10.dp),
        shape = RoundedCornerShape(8.dp),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceVariant)
    ) {
        Column {
            AsyncImage(
                model = track.hqThumbnail,
                contentDescription = null,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(110.dp)
                    .clip(RoundedCornerShape(topStart = 8.dp, topEnd = 8.dp)),
                contentScale = ContentScale.Crop
            )
            Text(
                text = track.title,
                style = MaterialTheme.typography.bodySmall,
                maxLines = 2,
                overflow = TextOverflow.Ellipsis,
                modifier = Modifier.padding(8.dp, 6.dp)
            )
        }
    }
}
