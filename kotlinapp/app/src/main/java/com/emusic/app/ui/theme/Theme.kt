package com.emusic.app.ui.theme

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

val EMusicDark = darkColorScheme(
    primary = Color(0xFF1DB954),           // verde Spotify-like
    onPrimary = Color.Black,
    primaryContainer = Color(0xFF14833B),
    secondary = Color(0xFF1DB954),
    background = Color(0xFF121212),
    surface = Color(0xFF1E1E1E),
    surfaceVariant = Color(0xFF2A2A2A),
    onBackground = Color.White,
    onSurface = Color.White,
    onSurfaceVariant = Color(0xFFB3B3B3),
    outline = Color(0xFF535353)
)

@Composable
fun EMusicTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = EMusicDark,
        content = content
    )
}
