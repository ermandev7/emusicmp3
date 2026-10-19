package com.emusic.shared

/**
 * Prueba de humo del módulo compartido (KMP): confirma que Android e iOS
 * pueden compilar contra el mismo código común. Fase 1 de la migración a
 * Kotlin Multiplatform — todavía no contiene lógica real de eMusic.
 */
expect fun currentPlatformName(): String

fun sharedGreeting(): String = "eMusic shared module corriendo en ${currentPlatformName()}"
