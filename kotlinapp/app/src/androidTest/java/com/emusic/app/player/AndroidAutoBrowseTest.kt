package com.emusic.app.player

import android.content.ComponentName
import android.content.Context
import androidx.datastore.preferences.core.edit
import androidx.datastore.preferences.core.stringPreferencesKey
import androidx.media3.session.LibraryResult
import androidx.media3.session.MediaBrowser
import androidx.media3.session.MediaLibraryService.LibraryParams
import androidx.media3.session.SessionToken
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.emusic.app.ui.setup.dataStore
import com.google.common.truth.Truth.assertThat
import kotlinx.coroutines.runBlocking
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/**
 * Replica lo que hace Android Auto con la tarjeta "Para ti" de su pantalla de
 * inicio: pide la raíz de sugerencias (isSuggested) y espera canciones
 * REPRODUCIBLES, no carpetas. Antes se devolvía la raíz normal y la tarjeta se
 * quedaba en "Contenido no disponible".
 *
 * Requiere red real (la API en la Pi). Solo lee: no reproduce ni escribe nada.
 */
@RunWith(AndroidJUnit4::class)
class AndroidAutoBrowseTest {

    private val context: Context = ApplicationProvider.getApplicationContext()
    private val instr = InstrumentationRegistry.getInstrumentation()
    private lateinit var browser: MediaBrowser

    @Before
    fun connect() {
        // Usuario con historial, para que haya algo que sugerir.
        runBlocking { context.dataStore.edit { it[stringPreferencesKey("user_id")] = TEST_USER } }
        val token = SessionToken(context, ComponentName(context, MusicService::class.java))
        val ref = AtomicReference<com.google.common.util.concurrent.ListenableFuture<MediaBrowser>>()
        instr.runOnMainSync { ref.set(MediaBrowser.Builder(context, token).buildAsync()) }
        browser = ref.get().get(20, TimeUnit.SECONDS)
    }

    @After
    fun release() {
        instr.runOnMainSync { browser.release() }
    }

    private fun <T> onMain(block: () -> com.google.common.util.concurrent.ListenableFuture<T>): T {
        val ref = AtomicReference<com.google.common.util.concurrent.ListenableFuture<T>>()
        instr.runOnMainSync { ref.set(block()) }
        return ref.get().get(60, TimeUnit.SECONDS)
    }

    @Test
    fun raizDeSugerencias_devuelveCancionesReproducibles() {
        val suggested = LibraryParams.Builder().setSuggested(true).build()
        val root = onMain { browser.getLibraryRoot(suggested) }
        assertThat(root.resultCode).isEqualTo(LibraryResult.RESULT_SUCCESS)
        assertThat(root.value!!.mediaId).isEqualTo(MusicService.SUGGESTED_ID)

        val children = onMain { browser.getChildren(root.value!!.mediaId, 0, 30, suggested) }
        assertThat(children.resultCode).isEqualTo(LibraryResult.RESULT_SUCCESS)
        val items = children.value!!
        assertThat(items).isNotEmpty()
        items.forEach {
            assertThat(it.mediaMetadata.isPlayable).isTrue()
            assertThat(it.mediaMetadata.title?.toString()).isNotEmpty()
        }

        // Paginación: Android Auto puede pedir la tarjeta por páginas.
        val page0 = onMain { browser.getChildren(root.value!!.mediaId, 0, 3, suggested) }.value!!
        val page1 = onMain { browser.getChildren(root.value!!.mediaId, 1, 3, suggested) }.value!!
        assertThat(page0.map { it.mediaId }).isEqualTo(items.take(3).map { it.mediaId })
        assertThat(page1.map { it.mediaId }).isEqualTo(items.drop(3).take(3).map { it.mediaId })

        // El registro de diagnóstico anota lo que pidió el "coche".
        val log = java.io.File(context.filesDir, "auto_diag.log").readText()
        assertThat(log).contains("-> SUGGESTED")
        assertThat(log).contains("getChildren 'SUGGESTED'")
    }

    @Test
    fun raizNormal_sigueSiendoLasCarpetas() {
        val root = onMain { browser.getLibraryRoot(null) }
        assertThat(root.value!!.mediaId).isEqualTo(MusicService.ROOT_ID)
        val children = onMain { browser.getChildren(MusicService.ROOT_ID, 0, 30, null) }
        assertThat(children.value!!.map { it.mediaId }).contains(MusicService.RECOMMENDED_ID)
    }

    @Test
    fun conUnaSolaCancion_siguienteSigueDisponible() {
        // Sin "siguiente" disponible, Android Auto cambia ⏮ ⏭ por ⏪10 ⏩10.
        val item = androidx.media3.common.MediaItem.Builder().setMediaId("dQw4w9WgXcQ").build()
        instr.runOnMainSync { browser.setMediaItems(listOf(item)) }
        var hasNext = true
        var nextAvailable = false
        var count = 0
        val deadline = System.currentTimeMillis() + 10_000
        while (System.currentTimeMillis() < deadline) {
            instr.runOnMainSync {
                count = browser.mediaItemCount
                hasNext = browser.hasNextMediaItem()
                nextAvailable = browser.isCommandAvailable(androidx.media3.common.Player.COMMAND_SEEK_TO_NEXT)
            }
            if (count == 1) break
            Thread.sleep(200)
        }
        assertThat(count).isEqualTo(1)
        assertThat(hasNext).isFalse()
        assertThat(nextAvailable).isTrue()
        instr.runOnMainSync { browser.clearMediaItems() }
    }

    @Test
    fun siguienteConColaAgotada_traeMasCancionesYSalta() {
        // Una canción sola, sin reproducir (no escribe historial): "siguiente" debe
        // pedir más al modo radio y saltar a la primera que llegue.
        val item = androidx.media3.common.MediaItem.Builder()
            .setMediaId("vqyRbmmy4r0")
            .setMediaMetadata(
                androidx.media3.common.MediaMetadata.Builder()
                    .setTitle("Que Fue Lo Que Me Hiciste").setArtist("Dalvin La Melodia").build()
            )
            .build()
        instr.runOnMainSync { browser.setMediaItems(listOf(item)) }
        Thread.sleep(1_000)
        instr.runOnMainSync { browser.seekToNext() }

        var count = 0
        var index = 0
        val deadline = System.currentTimeMillis() + 30_000 // la API tarda 5-7 s
        while (System.currentTimeMillis() < deadline) {
            instr.runOnMainSync {
                count = browser.mediaItemCount
                index = browser.currentMediaItemIndex
            }
            if (count > 1 && index == 1) break
            Thread.sleep(500)
        }
        assertThat(count).isGreaterThan(1)
        assertThat(index).isEqualTo(1)
        instr.runOnMainSync { browser.clearMediaItems() }
    }

    @Test
    fun protocoloAntiguo_comoAndroidAuto_sugerenciasReproducibles() {
        // Android Auto no usa el cliente de Media3 sino android.media.browse.MediaBrowser
        // con EXTRA_SUGGESTED en los rootHints; Media3 lo traduce. Se prueba esa vía.
        val hints = android.os.Bundle().apply {
            putBoolean(android.service.media.MediaBrowserService.BrowserRoot.EXTRA_SUGGESTED, true)
        }
        val connected = java.util.concurrent.CountDownLatch(1)
        var failed = false
        lateinit var legacy: android.media.browse.MediaBrowser
        instr.runOnMainSync {
            legacy = android.media.browse.MediaBrowser(
                context,
                ComponentName(context, MusicService::class.java),
                object : android.media.browse.MediaBrowser.ConnectionCallback() {
                    override fun onConnected() = connected.countDown()
                    override fun onConnectionFailed() { failed = true; connected.countDown() }
                },
                hints
            )
            legacy.connect()
        }
        assertThat(connected.await(20, TimeUnit.SECONDS)).isTrue()
        assertThat(failed).isFalse()

        var rootId = ""
        var rootSuggested = false
        instr.runOnMainSync {
            rootId = legacy.root
            rootSuggested = legacy.extras?.getBoolean(
                android.service.media.MediaBrowserService.BrowserRoot.EXTRA_SUGGESTED) == true
        }
        assertThat(rootId).isEqualTo(MusicService.SUGGESTED_ID)
        assertThat(rootSuggested).isTrue()

        val loaded = java.util.concurrent.CountDownLatch(1)
        var children: List<android.media.browse.MediaBrowser.MediaItem> = emptyList()
        instr.runOnMainSync {
            legacy.subscribe(rootId, object : android.media.browse.MediaBrowser.SubscriptionCallback() {
                override fun onChildrenLoaded(
                    parentId: String,
                    items: MutableList<android.media.browse.MediaBrowser.MediaItem>
                ) {
                    children = items.toList()
                    loaded.countDown()
                }
            })
        }
        assertThat(loaded.await(60, TimeUnit.SECONDS)).isTrue()
        instr.runOnMainSync { legacy.disconnect() }

        assertThat(children).isNotEmpty()
        children.forEach {
            assertThat(it.isPlayable).isTrue()
            assertThat(it.description.title?.toString()).isNotEmpty()
        }

        // Carátulas: Android Auto solo carga content:// (no https). Deben venir así y
        // poder abrirse (el provider descarga y cachea la imagen).
        val withArt = children.mapNotNull { it.description.iconUri }
        assertThat(withArt).isNotEmpty()
        withArt.forEach { assertThat(it.scheme).isEqualTo("content") }
        val bytes = context.contentResolver.openInputStream(withArt.first())!!.use { it.readBytes() }
        assertThat(bytes.size).isGreaterThan(1000)
    }

    private companion object { const val TEST_USER = "erman" }
}
