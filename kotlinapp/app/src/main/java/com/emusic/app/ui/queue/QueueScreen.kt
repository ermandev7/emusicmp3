package com.emusic.app.ui.queue

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGesturesAfterLongPress
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.DragHandle
import androidx.compose.material.icons.filled.KeyboardArrowDown
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.zIndex
import androidx.hilt.navigation.compose.hiltViewModel
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import coil.compose.AsyncImage
import com.emusic.app.ui.components.NowPlayingBars
import com.emusic.app.ui.player.PlayerViewModel
import kotlin.math.roundToInt

@Composable
fun QueueScreen(
    onBack: () -> Unit,
    playerViewModel: PlayerViewModel = hiltViewModel()
) {
    val state by playerViewModel.state.collectAsStateWithLifecycle()

    // Estado del arrastre para reordenar.
    var draggingIndex by remember { mutableStateOf<Int?>(null) }
    var dragOffset by remember { mutableFloatStateOf(0f) }
    var itemHeightPx by remember { mutableFloatStateOf(0f) }
    val listState = rememberLazyListState()

    Column(
        modifier = Modifier
            .fillMaxSize()
            .statusBarsPadding()
    ) {
        // Header inline.
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .padding(horizontal = 8.dp, vertical = 8.dp),
            verticalAlignment = Alignment.CenterVertically
        ) {
            IconButton(onClick = onBack, modifier = Modifier.size(40.dp)) {
                Icon(Icons.Default.KeyboardArrowDown, "Cerrar", modifier = Modifier.size(28.dp))
            }
            Spacer(Modifier.width(4.dp))
            Column {
                Text("Cola de reproducción", style = MaterialTheme.typography.titleMedium, fontWeight = FontWeight.Bold)
                if (state.queue.isNotEmpty()) {
                    Text(
                        "${state.queue.size} canciones · mantén pulsado para reordenar",
                        style = MaterialTheme.typography.labelSmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }
        }

        if (state.queue.isEmpty()) {
            Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
                Text("La cola está vacía", color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        } else {
            LazyColumn(state = listState) {
                itemsIndexed(state.queue, key = { _, t -> t.videoId }) { index, track ->
                    val isCurrent = index == state.currentIndex
                    val isDragging = draggingIndex == index

                    Row(
                        modifier = Modifier
                            .fillMaxWidth()
                            .onSizeChanged { if (itemHeightPx == 0f) itemHeightPx = it.height.toFloat() }
                            .zIndex(if (isDragging) 1f else 0f)
                            .graphicsLayer {
                                if (isDragging) {
                                    translationY = dragOffset
                                    shadowElevation = 12f
                                }
                            }
                            .then(
                                if (isCurrent) Modifier.background(
                                    MaterialTheme.colorScheme.primary.copy(alpha = 0.12f)
                                ) else Modifier
                            )
                            .clickable { playerViewModel.seekToIndex(index) }
                            .padding(horizontal = 12.dp, vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        AsyncImage(
                            model = track.displayThumbnail,
                            contentDescription = null,
                            modifier = Modifier.size(48.dp).clip(RoundedCornerShape(6.dp)),
                            contentScale = ContentScale.Crop
                        )
                        Spacer(Modifier.width(12.dp))
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                track.title,
                                style = MaterialTheme.typography.bodyMedium,
                                color = if (isCurrent) MaterialTheme.colorScheme.primary
                                        else MaterialTheme.colorScheme.onSurface,
                                maxLines = 1, overflow = TextOverflow.Ellipsis
                            )
                            Text(
                                track.displayArtist,
                                style = MaterialTheme.typography.labelSmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1, overflow = TextOverflow.Ellipsis
                            )
                        }

                        if (isCurrent && state.isPlaying) {
                            NowPlayingBars(modifier = Modifier.padding(horizontal = 6.dp))
                        }

                        // Quitar de la cola.
                        IconButton(onClick = { playerViewModel.removeQueueItem(index) }, modifier = Modifier.size(36.dp)) {
                            Icon(Icons.Default.Close, "Quitar", modifier = Modifier.size(18.dp),
                                tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        }

                        // Asa de arrastre: mantener pulsado y arrastrar para reordenar.
                        Icon(
                            Icons.Default.DragHandle, "Reordenar",
                            tint = MaterialTheme.colorScheme.onSurfaceVariant,
                            modifier = Modifier
                                .size(28.dp)
                                .pointerInput(track.videoId) {
                                    detectDragGesturesAfterLongPress(
                                        onDragStart = { draggingIndex = index; dragOffset = 0f },
                                        onDrag = { change, amount ->
                                            change.consume()
                                            dragOffset += amount.y
                                        },
                                        onDragEnd = {
                                            val from = draggingIndex
                                            if (from != null && itemHeightPx > 0f) {
                                                val shift = (dragOffset / itemHeightPx).roundToInt()
                                                val to = (from + shift).coerceIn(0, state.queue.size - 1)
                                                if (to != from) playerViewModel.moveQueueItem(from, to)
                                            }
                                            draggingIndex = null; dragOffset = 0f
                                        },
                                        onDragCancel = { draggingIndex = null; dragOffset = 0f }
                                    )
                                }
                        )
                    }
                }
                item { Spacer(Modifier.height(80.dp)) }
            }
        }
    }
}
