package com.emusic.app.navigation

import androidx.compose.runtime.Composable
import androidx.navigation.NavHostController
import androidx.navigation.NavType
import androidx.navigation.compose.NavHost
import androidx.navigation.compose.composable
import androidx.navigation.navArgument
import com.emusic.app.ui.home.HomeScreen
import com.emusic.app.ui.library.LibraryScreen
import com.emusic.app.ui.player.PlayerScreen
import com.emusic.app.ui.playlist.PlaylistDetailScreen
import com.emusic.app.ui.queue.QueueScreen
import com.emusic.app.ui.search.SearchScreen
import com.emusic.app.ui.setup.UserSetupScreen

sealed class Screen(val route: String) {
    object Setup : Screen("setup")
    object Home : Screen("home")
    object Search : Screen("search")
    object Library : Screen("library")
    object Player : Screen("player")
    object Queue : Screen("queue")
    object PlaylistDetail : Screen("playlist/{id}/{name}") {
        fun createRoute(id: Int, name: String) = "playlist/$id/${java.net.URLEncoder.encode(name, "UTF-8")}"
    }
}

@Composable
fun AppNavigation(navController: NavHostController, startDestination: String) {
    NavHost(navController = navController, startDestination = startDestination) {
        composable(Screen.Setup.route) {
            UserSetupScreen(onSetupComplete = {
                navController.navigate(Screen.Home.route) {
                    popUpTo(Screen.Setup.route) { inclusive = true }
                }
            })
        }
        composable(Screen.Home.route) {
            HomeScreen(
                onTrackClick = { navController.navigate(Screen.Player.route) },
                onSearchClick = { navController.navigate(Screen.Search.route) }
            )
        }
        composable(Screen.Search.route) {
            SearchScreen(
                onTrackClick = { navController.navigate(Screen.Player.route) },
                onBack = { navController.popBackStack() }
            )
        }
        composable(Screen.Library.route) {
            LibraryScreen(
                onTrackClick = { navController.navigate(Screen.Player.route) },
                onPlaylistClick = { id, name ->
                    navController.navigate(Screen.PlaylistDetail.createRoute(id, name))
                }
            )
        }
        composable(Screen.Player.route) {
            PlayerScreen(
                onQueueClick = { navController.navigate(Screen.Queue.route) },
                onBack = { navController.popBackStack() }
            )
        }
        composable(Screen.Queue.route) {
            QueueScreen(onBack = { navController.popBackStack() })
        }
        composable(
            route = Screen.PlaylistDetail.route,
            arguments = listOf(
                navArgument("id") { type = NavType.IntType },
                navArgument("name") { type = NavType.StringType }
            )
        ) { backStack ->
            val id = backStack.arguments?.getInt("id") ?: 0
            val name = java.net.URLDecoder.decode(backStack.arguments?.getString("name") ?: "", "UTF-8")
            PlaylistDetailScreen(
                playlistId = id,
                playlistName = name,
                onTrackClick = { navController.navigate(Screen.Player.route) },
                onBack = { navController.popBackStack() }
            )
        }
    }
}
