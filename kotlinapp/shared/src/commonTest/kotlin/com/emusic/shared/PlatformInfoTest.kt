package com.emusic.shared

import kotlin.test.Test
import kotlin.test.assertTrue

class PlatformInfoTest {
    @Test
    fun sharedGreeting_incluyeElNombreDeLaPlataforma() {
        val greeting = sharedGreeting()
        assertTrue(greeting.contains(currentPlatformName()))
    }
}
