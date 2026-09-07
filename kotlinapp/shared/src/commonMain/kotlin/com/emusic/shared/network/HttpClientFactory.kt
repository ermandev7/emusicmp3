package com.emusic.shared.network

import io.ktor.client.HttpClient
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.client.plugins.logging.LogLevel
import io.ktor.client.plugins.logging.Logging
import io.ktor.serialization.kotlinx.json.json
import kotlinx.serialization.json.Json

/**
 * Fase 3 de la migración a Kotlin Multiplatform: cliente HTTP compartido con Ktor,
 * reemplazo de Retrofit (JVM-only, no corre en Kotlin/Native/iOS). El motor real
 * (OkHttp en Android, Darwin en iOS) lo resuelve Ktor automáticamente según la
 * dependencia de cada source set (ver shared/build.gradle.kts) — por eso acá no
 * hace falta un expect/actual para el engine.
 */
fun createEMusicHttpClient(): HttpClient = HttpClient {
    install(ContentNegotiation) {
        json(Json {
            ignoreUnknownKeys = true // el backend puede agregar campos nuevos sin romper la app
            isLenient = true
            coerceInputValues = true
        })
    }
    install(HttpTimeout) {
        connectTimeoutMillis = 10_000
        requestTimeoutMillis = 25_000
    }
    install(Logging) {
        level = LogLevel.INFO
    }
    // Los métodos de MusicApiClient asumen que una respuesta no-2xx lanza
    // excepción (igual que Retrofit + su try/catch de siempre en MusicRepository).
    expectSuccess = true
}
