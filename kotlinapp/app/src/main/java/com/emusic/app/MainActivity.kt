package com.emusic.app

import android.Manifest
import android.content.Intent
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Home
import androidx.compose.material.icons.filled.LibraryMusic
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.lifecycleScope
import androidx.navigation.compose.currentBackStackEntryAsState
import androidx.navigation.compose.rememberNavController
import com.emusic.app.navigation.AppNavigation
import com.emusic.app.navigation.Screen
import com.emusic.app.player.MusicController
import com.emusic.app.player.PlayMediaCommand
import com.emusic.app.ui.components.MiniPlayer
import com.emusic.app.ui.player.PlayerViewModel
import com.emusic.app.ui.setup.USER_ID_KEY
import com.emusic.app.ui.setup.dataStore
import com.emusic.app.ui.theme.EMusicTheme
import com.emusic.app.voice.VoiceAction
import com.emusic.app.voice.VoiceAssistant
import dagger.hilt.android.AndroidEntryPoint
import kotlinx.coroutines.flow.firstOrNull
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import javax.inject.Inject

@AndroidEntryPoint
class MainActivity : ComponentActivity() {

    @Inject lateinit var musicController: MusicController
    @Inject lateinit var voiceAssistant: VoiceAssistant
    @Inject lateinit var playMediaCommand: PlayMediaCommand

    private val permissionLauncher = registerForActivityResult(
        ActivityResultContracts.RequestMultiplePermissions()
    ) { /* permisos concedidos */ }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()

        // Permiso de lectura de medios para VER todas las descargas en el dispositivo
        // (no solo las que esta instalación creó). API 33+ usa READ_MEDIA_AUDIO; antes,
        // READ_EXTERNAL_STORAGE.
        val mediaPerm = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU)
            Manifest.permission.READ_MEDIA_AUDIO
        else
            Manifest.permission.READ_EXTERNAL_STORAGE
        permissionLauncher.launch(
            arrayOf(
                Manifest.permission.RECORD_AUDIO,
                Manifest.permission.POST_NOTIFICATIONS,
                mediaPerm
            )
        )

        musicController.connect()
        handlePlayMediaIntent(intent)

        var startDestination = Screen.Home.route

        setContent {
            EMusicTheme {
                var resolvedStart by remember { mutableStateOf<String?>(null) }

                LaunchedEffect(Unit) {
                    val userId = dataStore.data
                        .map { it[USER_ID_KEY] }
                        .firstOrNull()
                    resolvedStart = if (userId.isNullOrEmpty()) Screen.Setup.route else Screen.Home.route
                }

                if (resolvedStart != null) {
                    MainScaffold(
                        startDestination = resolvedStart!!,
                        voiceAssistant = voiceAssistant,
                        musicController = musicController,
                        playMediaCommand = playMediaCommand
                    )
                }
            }
        }
    }

    // App ya abierta (launchMode singleTop): el Asistente reenvía el intent aquí.
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        handlePlayMediaIntent(intent)
    }

    /** App Actions PLAY_MEDIA: si el intent trae una consulta, busca y reproduce. */
    private fun handlePlayMediaIntent(intent: Intent?) {
        val query = intent?.getStringExtra("query")?.trim().orEmpty()
        if (query.isEmpty()) return
        val artist = intent?.getStringExtra("artist")?.trim()
        lifecycleScope.launch {
            playMediaCommand.searchAndPlay(query, artist)
        }
    }

    override fun onDestroy() {
        musicController.disconnect()
        voiceAssistant.destroy()
        super.onDestroy()
    }
}

@Composable
private fun MainScaffold(
    startDestination: String,
    voiceAssistant: VoiceAssistant,
    musicController: MusicController,
    playMediaCommand: PlayMediaCommand
) {
    val navController = rememberNavController()
    val backStack by navController.currentBackStackEntryAsState()
    val currentRoute = backStack?.destination?.route

    // Único punto de manejo de comandos de voz (evita carreras con otras pantallas).
    // Se enclava en commandId para reaccionar a cada reconocimiento, incluso si se
    // repite el mismo comando ("siguiente" dos veces).
    val voiceState by voiceAssistant.state.collectAsStateWithLifecycle()
    LaunchedEffect(voiceState.commandId) {
        val cmd = voiceState.command ?: return@LaunchedEffect
        when (cmd.action) {
            // "reproduce bon jovi": busca y reproduce el primer resultado, encola el
            // resto, y abre el reproductor para feedback inmediato.
            VoiceAction.Play -> {
                if (cmd.query.isNotEmpty()) {
                    if (currentRoute != Screen.Player.route) {
                        navController.navigate(Screen.Player.route) { launchSingleTop = true }
                    }
                    musicController.setLoading()
                    val ok = playMediaCommand.searchAndPlay(cmd.query, cmd.artist)
                    if (!ok) musicController.clearLoading()
                }
            }
            // Comandos de transporte: controlan la reproducción en curso.
            VoiceAction.Next -> musicController.seekToNext()
            VoiceAction.Previous -> musicController.seekToPrevious()
            VoiceAction.Pause, VoiceAction.Stop -> musicController.pause()
            VoiceAction.Resume -> musicController.play()
            else -> Unit
        }
        voiceAssistant.clearCommand()
    }

    val showBottomNav = currentRoute in listOf(Screen.Home.route, Screen.Search.route, Screen.Library.route)
    val showMiniPlayer = currentRoute != Screen.Player.route &&
        currentRoute != Screen.Setup.route

    Scaffold(
        bottomBar = {
            Column {
                if (showMiniPlayer) {
                    val playerViewModel: PlayerViewModel = androidx.hilt.navigation.compose.hiltViewModel()
                    val playerState by playerViewModel.state.collectAsStateWithLifecycle()
                    if (playerState.currentTrack != null) {
                        MiniPlayer(
                            state = playerState,
                            onExpandClick = { navController.navigate(Screen.Player.route) },
                            onPlayPause = { if (playerState.isPlaying) playerViewModel.pause() else playerViewModel.play() },
                            onNext = { playerViewModel.next() },
                            onPrevious = { playerViewModel.previous() },
                            positionProvider = { playerViewModel.getPosition() },
                            durationProvider = { playerViewModel.getDuration() }
                        )
                    }
                }

                if (showBottomNav) {
                    NavigationBar {
                        NavigationBarItem(
                            icon = { Icon(Icons.Default.Home, "Inicio") },
                            label = { Text("Inicio") },
                            selected = currentRoute == Screen.Home.route,
                            onClick = {
                                navController.navigate(Screen.Home.route) {
                                    popUpTo(Screen.Home.route) { inclusive = true }
                                }
                            }
                        )
                        NavigationBarItem(
                            icon = { Icon(Icons.Default.Search, "Buscar") },
                            label = { Text("Buscar") },
                            selected = currentRoute == Screen.Search.route,
                            onClick = { navController.navigate(Screen.Search.route) }
                        )
                        NavigationBarItem(
                            icon = { Icon(Icons.Default.LibraryMusic, "Biblioteca") },
                            label = { Text("Biblioteca") },
                            selected = currentRoute == Screen.Library.route,
                            onClick = { navController.navigate(Screen.Library.route) }
                        )
                    }
                }
            }
        }
    ) { padding ->
        Box(modifier = Modifier.padding(padding)) {
            AppNavigation(navController = navController, startDestination = startDestination)
        }
    }
}
