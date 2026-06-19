package com.emusic.app.ui.player

import android.graphics.drawable.BitmapDrawable
import androidx.compose.animation.animateColorAsState
import androidx.compose.animation.core.tween
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.palette.graphics.Palette
import coil.imageLoader
import coil.request.ImageRequest
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * Extrae el color dominante de la carátula y lo devuelve animado.
 * Se usa para pintar un gradiente de fondo dinámico en la pantalla de reproducción.
 */
@Composable
fun rememberDominantColor(imageUrl: String?, fallback: Color): Color {
    val context = LocalContext.current
    var target by remember(imageUrl) { mutableStateOf(fallback) }

    LaunchedEffect(imageUrl) {
        if (imageUrl.isNullOrEmpty()) {
            target = fallback
            return@LaunchedEffect
        }
        val request = ImageRequest.Builder(context)
            .data(imageUrl)
            .allowHardware(false) // Palette necesita acceso a los píxeles
            .size(180)
            .build()
        val drawable = context.imageLoader.execute(request).drawable
        val bitmap = (drawable as? BitmapDrawable)?.bitmap ?: return@LaunchedEffect
        val palette = withContext(Dispatchers.Default) { Palette.from(bitmap).generate() }
        val swatch = palette.darkVibrantSwatch
            ?: palette.vibrantSwatch
            ?: palette.darkMutedSwatch
            ?: palette.mutedSwatch
            ?: palette.dominantSwatch
        if (swatch != null) target = Color(swatch.rgb)
    }

    val animated by animateColorAsState(
        targetValue = target,
        animationSpec = tween(durationMillis = 700),
        label = "dominantColor"
    )
    return animated
}
