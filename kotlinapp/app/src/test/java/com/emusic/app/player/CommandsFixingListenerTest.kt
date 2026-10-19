package com.emusic.app.player

import androidx.media3.common.Player
import com.google.common.truth.Truth.assertThat
import org.junit.Test

/**
 * Player.Listener solo tiene métodos `default`: si [CommandsFixingListener] no
 * sobrescribe uno, ese aviso se pierde sin error de compilación (pasó con
 * `by delegate` y la sesión dejó de enterarse de qué sonaba). Este test falla si
 * falta alguno, también si una versión nueva de Media3 añade avisos.
 */
class CommandsFixingListenerTest {

    @Test
    fun `reenvía todos los métodos de Player Listener`() {
        val missing = Player.Listener::class.java.methods
            .filter { it.declaringClass == Player.Listener::class.java }
            .filterNot { m ->
                runCatching {
                    CommandsFixingListener::class.java.getDeclaredMethod(m.name, *m.parameterTypes)
                }.isSuccess
            }
            .map { m -> "${m.name}(${m.parameterTypes.joinToString { it.simpleName }})" }
        assertThat(missing).isEmpty()
    }
}
