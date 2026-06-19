package com.emusic.app.player

import android.content.ComponentName
import android.content.Context
import androidx.media3.common.MediaItem
import androidx.media3.session.MediaController
import androidx.media3.session.SessionToken
import androidx.test.core.app.ApplicationProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.google.common.truth.Truth.assertThat
import org.junit.Test
import org.junit.runner.RunWith
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicReference

/**
 * Test de integración del comando de voz de Android Auto / Gemini.
 *
 * Replica exactamente lo que hace Android Auto al decir "pon música de bon jovi":
 * conecta un MediaController al MusicService y hace setMediaItems con un MediaItem
 * cuyo requestMetadata.searchQuery es la consulta. Verifica que el servicio busca,
 * resuelve la cola y arranca la reproducción.
 *
 * Requiere red real (la API en la Pi). Corre en el dispositivo conectado.
 */
@RunWith(AndroidJUnit4::class)
class AndroidAutoVoiceTest {

    private val context: Context = ApplicationProvider.getApplicationContext()
    private val instr = InstrumentationRegistry.getInstrumentation()

    @Test
    fun voiceCommand_bonJovi_buscaYReproduce() {
        val token = SessionToken(context, ComponentName(context, MusicService::class.java))

        // Construir el MediaController en el main looper, esperar la conexión en el test thread.
        val futureRef = AtomicReference<com.google.common.util.concurrent.ListenableFuture<MediaController>>()
        instr.runOnMainSync {
            futureRef.set(MediaController.Builder(context, token).buildAsync())
        }
        val controller = futureRef.get().get(20, TimeUnit.SECONDS)

        try {
            // Igual que Gemini/Android Auto: un MediaItem solo con searchQuery.
            instr.runOnMainSync {
                val voiceItem = MediaItem.Builder()
                    .setRequestMetadata(
                        MediaItem.RequestMetadata.Builder()
                            .setSearchQuery("bon jovi")
                            .build()
                    )
                    .build()
                controller.setMediaItems(listOf(voiceItem))
                controller.prepare()
                controller.play()
            }

            // Esperar hasta 45s a que arranque la reproducción (la API tarda ~9s).
            var itemCount = 0
            var isPlaying = false
            val deadline = System.currentTimeMillis() + 45_000
            while (System.currentTimeMillis() < deadline) {
                instr.runOnMainSync {
                    itemCount = controller.mediaItemCount
                    isPlaying = controller.isPlaying
                }
                if (isPlaying) break
                Thread.sleep(500)
            }

            // La búsqueda por voz devolvió una cola de canciones...
            assertThat(itemCount).isGreaterThan(0)
            // ...y empezó a reproducir.
            assertThat(isPlaying).isTrue()
        } finally {
            instr.runOnMainSync {
                controller.stop()
                controller.release()
            }
        }
    }
}
