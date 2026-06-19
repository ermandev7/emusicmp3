package com.emusic.app.ui.player

import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.animateFloatAsState
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectHorizontalDragGestures
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.*
import androidx.compose.material.icons.outlined.FavoriteBorder
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.media3.common.Player
import coil.compose.AsyncImage
import com.emusic.app.ui.components.formatDuration
import kotlinx.coroutines.delay

@Composable
fun PlayerScreen(
    onQueueClick: () -> Unit,
    onBack: () -> Unit,
    viewModel: PlayerViewModel = hiltViewModel()
) {
    val state by viewModel.state.collectAsStateWithLifecycle()
    val extras by viewModel.extras.collectAsStateWithLifecycle()
    val loadingTrack by viewModel.loadingTrack.collectAsStateWithLifecycle()

    // Mientras se resuelve la URL mostramos el track que se está cargando.
    val isResolving = loadingTrack != null
    val track = loadingTrack ?: state.currentTrack

    LaunchedEffect(track?.videoId) {
        track?.videoId?.let { viewModel.checkFavorite(it) }
    }

    var positionMs by remember { mutableLongStateOf(0L) }
    LaunchedEffect(state.isPlaying) {
        while (state.isPlaying) {
            positionMs = viewModel.getPosition()
            delay(1000L)
        }
    }
    LaunchedEffect(state.currentTrack) { positionMs = 0L }

    // Color dominante de la carátula → gradiente de fondo dinámico.
    val dominant = rememberDominantColor(
        track?.hqThumbnail ?: track?.displayThumbnail,
        MaterialTheme.colorScheme.surfaceVariant
    )
    val background = MaterialTheme.colorScheme.background

    // Swipe-down para minimizar.
    var dragOffsetY by remember { mutableFloatStateOf(0f) }
    var dismissed by remember { mutableStateOf(false) }

    // Mientras se resuelve el stream, atenuamos el contenido real (foto/título se
    // siguen leyendo) en vez de taparlo con un skeleton opaco. Al sonar → opacidad 1.
    val contentAlpha by animateFloatAsState(
        targetValue = if (isResolving && state.error == null) 0.45f else 1f,
        animationSpec = tween(300),
        label = "loadingAlpha"
    )

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(
                Brush.verticalGradient(
                    colors = listOf(dominant.copy(alpha = 0.55f), background, background)
                )
            )
    ) {
        Column(
            modifier = Modifier
                .fillMaxSize()
                .graphicsLayer {
                    translationY = dragOffsetY.coerceAtLeast(0f)
                    alpha = (1f - (dragOffsetY.coerceAtLeast(0f) / 1200f).coerceIn(0f, 0.5f)) * contentAlpha
                }
                .pointerInput(Unit) {
                    detectDragGestures(
                        onDragEnd = {
                            if (dragOffsetY > 300f && !dismissed) {
                                dismissed = true
                                onBack()
                            }
                            dragOffsetY = 0f
                        },
                        onDragCancel = { dragOffsetY = 0f },
                        onDrag = { change, dragAmount ->
                            if (dragAmount.y > 0 || dragOffsetY > 0) {
                                change.consume()
                                dragOffsetY = (dragOffsetY + dragAmount.y).coerceAtLeast(0f)
                            }
                        }
                    )
                }
                .statusBarsPadding()
                .padding(horizontal = 24.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            // Indicador de swipe (pill).
            Box(
                modifier = Modifier
                    .padding(top = 10.dp)
                    .width(40.dp)
                    .height(4.dp)
                    .clip(RoundedCornerShape(2.dp))
                    .background(MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.35f))
            )

            // Barra superior.
            Row(
                modifier = Modifier
                    .fillMaxWidth()
                    .padding(top = 6.dp, bottom = 6.dp),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                IconButton(onClick = onBack, modifier = Modifier.size(40.dp)) {
                    Icon(Icons.Default.KeyboardArrowDown, "Cerrar", modifier = Modifier.size(28.dp))
                }
                Text(
                    "REPRODUCIENDO",
                    style = MaterialTheme.typography.labelSmall,
                    fontWeight = FontWeight.SemiBold,
                    letterSpacing = 1.6.sp,
                    color = MaterialTheme.colorScheme.onSurfaceVariant
                )
                IconButton(onClick = onQueueClick, modifier = Modifier.size(40.dp)) {
                    Icon(Icons.Default.QueueMusic, "Cola", modifier = Modifier.size(22.dp))
                }
            }

            Spacer(Modifier.weight(0.35f))

            // Carátula: escala animada según reproducción + sombra + swipe horizontal.
            var swipeOffset by remember { mutableFloatStateOf(0f) }
            val artScale by animateFloatAsState(
                targetValue = if (state.isPlaying) 1f else 0.9f,
                animationSpec = tween(350),
                label = "artScale"
            )

            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .aspectRatio(1f)
                    .graphicsLayer {
                        translationX = swipeOffset
                        scaleX = artScale
                        scaleY = artScale
                        alpha = 1f - (kotlin.math.abs(swipeOffset) / 800f).coerceIn(0f, 0.3f)
                    }
                    .shadow(28.dp, RoundedCornerShape(20.dp), clip = false)
                    .pointerInput(Unit) {
                        detectHorizontalDragGestures(
                            onDragEnd = {
                                when {
                                    swipeOffset < -200f -> viewModel.next()
                                    swipeOffset > 200f -> viewModel.previous()
                                }
                                swipeOffset = 0f
                            },
                            onDragCancel = { swipeOffset = 0f },
                            onHorizontalDrag = { change, amount ->
                                change.consume()
                                swipeOffset += amount
                            }
                        )
                    }
                    .clip(RoundedCornerShape(20.dp)),
                contentAlignment = Alignment.Center
            ) {
                AsyncImage(
                    model = track?.hqThumbnail ?: track?.displayThumbnail,
                    contentDescription = "Carátula",
                    modifier = Modifier.fillMaxSize(),
                    contentScale = ContentScale.Crop
                )
                if (state.error != null) {
                    Box(
                        modifier = Modifier
                            .fillMaxSize()
                            .background(Color.Black.copy(alpha = 0.6f)),
                        contentAlignment = Alignment.Center
                    ) {
                        Column(horizontalAlignment = Alignment.CenterHorizontally) {
                            Icon(
                                Icons.Default.ErrorOutline, null,
                                tint = Color.White, modifier = Modifier.size(40.dp)
                            )
                            Spacer(Modifier.height(8.dp))
                            Text("No se pudo reproducir", style = MaterialTheme.typography.bodyMedium, color = Color.White)
                            Spacer(Modifier.height(12.dp))
                            FilledTonalButton(onClick = { viewModel.retry() }) {
                                Icon(Icons.Default.Refresh, null, modifier = Modifier.size(18.dp))
                                Spacer(Modifier.width(6.dp))
                                Text("Reintentar")
                            }
                        }
                    }
                } else if (isResolving) {
                    // Sin scrim: la carátula se sigue viendo (atenuada por contentAlpha),
                    // solo un spinner centrado indica que está cargando.
                    CircularProgressIndicator(
                        color = Color.White,
                        strokeWidth = 3.dp,
                        modifier = Modifier.size(48.dp)
                    )
                }
            }

            Spacer(Modifier.weight(0.2f))
            Spacer(Modifier.height(24.dp))

            // Título + Artista + Acciones.
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    Text(
                        text = track?.title ?: "Sin reproducción",
                        style = MaterialTheme.typography.headlineSmall.copy(fontWeight = FontWeight.Bold),
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                    Spacer(Modifier.height(2.dp))
                    Text(
                        text = track?.displayArtist ?: "",
                        style = MaterialTheme.typography.bodyLarge,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }

                val heartColor by animateColorAsState(
                    if (extras.isFavorite) Color(0xFFE91E63) else MaterialTheme.colorScheme.onSurfaceVariant,
                    label = "heart"
                )
                IconButton(onClick = { viewModel.toggleFavorite() }, modifier = Modifier.size(44.dp)) {
                    Icon(
                        if (extras.isFavorite) Icons.Filled.Favorite else Icons.Outlined.FavoriteBorder,
                        "Me gusta", tint = heartColor, modifier = Modifier.size(26.dp)
                    )
                }

                IconButton(
                    onClick = { viewModel.downloadTrack() },
                    enabled = !extras.isDownloading,
                    modifier = Modifier.size(44.dp)
                ) {
                    if (extras.isDownloading) {
                        CircularProgressIndicator(modifier = Modifier.size(20.dp), strokeWidth = 2.dp)
                    } else {
                        Icon(
                            if (extras.downloadDone) Icons.Default.DownloadDone else Icons.Default.Download,
                            "Descargar",
                            tint = if (extras.downloadDone) MaterialTheme.colorScheme.primary
                                   else MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier.size(26.dp)
                        )
                    }
                }
            }

            Spacer(Modifier.height(20.dp))

            // Progreso: mientras se resuelve el stream (5-7s) mostramos una barra
            // atenuada con tiempos "--:--"; slider real cuando ya hay reproducción.
            if (isResolving) {
                Box(
                    Modifier.fillMaxWidth().padding(vertical = 18.dp)
                        .height(4.dp).clip(RoundedCornerShape(2.dp))
                        .background(MaterialTheme.colorScheme.onSurface.copy(alpha = 0.15f))
                )
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text("--:--", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text("--:--", style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            } else {
                val duration = viewModel.getDuration().coerceAtLeast(1L)
                Slider(
                    value = (positionMs.toFloat() / duration.toFloat()).coerceIn(0f, 1f),
                    onValueChange = { viewModel.seekTo((it * duration).toLong()) },
                    colors = SliderDefaults.colors(
                        thumbColor = MaterialTheme.colorScheme.primary,
                        activeTrackColor = MaterialTheme.colorScheme.primary,
                        inactiveTrackColor = MaterialTheme.colorScheme.onSurface.copy(alpha = 0.2f)
                    ),
                    modifier = Modifier.fillMaxWidth()
                )
                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                    Text(formatDuration((positionMs / 1000).toInt()), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    Text(formatDuration((duration / 1000).toInt()), style = MaterialTheme.typography.labelSmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }

            Spacer(Modifier.height(16.dp))

            // Controles de reproducción.
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceEvenly,
                verticalAlignment = Alignment.CenterVertically
            ) {
                IconButton(onClick = { viewModel.toggleShuffle() }, modifier = Modifier.size(44.dp)) {
                    Icon(
                        Icons.Default.Shuffle, "Aleatorio",
                        modifier = Modifier.size(24.dp),
                        tint = if (state.shuffleEnabled) MaterialTheme.colorScheme.primary
                               else MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }

                IconButton(onClick = { viewModel.previous() }, modifier = Modifier.size(60.dp)) {
                    Icon(Icons.Default.SkipPrevious, "Anterior", modifier = Modifier.size(40.dp))
                }

                FilledIconButton(
                    onClick = { if (state.isPlaying) viewModel.pause() else viewModel.play() },
                    enabled = !isResolving,
                    modifier = Modifier.size(72.dp),
                    shape = CircleShape
                ) {
                    if (isResolving || state.isBuffering) {
                        CircularProgressIndicator(
                            modifier = Modifier.size(28.dp),
                            color = MaterialTheme.colorScheme.onPrimary,
                            strokeWidth = 2.5.dp
                        )
                    } else {
                        Icon(
                            if (state.isPlaying) Icons.Default.Pause else Icons.Default.PlayArrow,
                            if (state.isPlaying) "Pausar" else "Reproducir",
                            modifier = Modifier.size(40.dp)
                        )
                    }
                }

                IconButton(onClick = { viewModel.next() }, modifier = Modifier.size(60.dp)) {
                    Icon(Icons.Default.SkipNext, "Siguiente", modifier = Modifier.size(40.dp))
                }

                IconButton(onClick = { viewModel.cycleRepeat() }, modifier = Modifier.size(44.dp)) {
                    Icon(
                        when (state.repeatMode) {
                            Player.REPEAT_MODE_ONE -> Icons.Default.RepeatOne
                            else -> Icons.Default.Repeat
                        },
                        "Repetir",
                        modifier = Modifier.size(24.dp),
                        tint = if (state.repeatMode != Player.REPEAT_MODE_OFF) MaterialTheme.colorScheme.primary
                               else MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }

            Spacer(Modifier.weight(1f))
        }
    }
}
