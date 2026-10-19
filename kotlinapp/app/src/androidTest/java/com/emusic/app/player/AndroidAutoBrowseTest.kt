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
    }

    @Test
    fun raizNormal_sigueSiendoLasCarpetas() {
        val root = onMain { browser.getLibraryRoot(null) }
        assertThat(root.value!!.mediaId).isEqualTo(MusicService.ROOT_ID)
        val children = onMain { browser.getChildren(MusicService.ROOT_ID, 0, 30, null) }
        assertThat(children.value!!.map { it.mediaId }).contains(MusicService.RECOMMENDED_ID)
    }

    private companion object { const val TEST_USER = "erman" }
}
