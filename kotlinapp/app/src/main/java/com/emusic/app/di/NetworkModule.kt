package com.emusic.app.di

import android.content.Context
import androidx.datastore.preferences.core.stringPreferencesKey
import com.emusic.app.ui.setup.dataStore
import com.emusic.shared.network.EMusicNetworkClient
import com.emusic.shared.network.MusicApiClient
import com.emusic.shared.network.PipedFallbackClient
import com.emusic.shared.network.createEMusicHttpClient
import com.emusic.shared.network.createPipedFallbackHttpClient
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import io.ktor.client.HttpClient
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import javax.inject.Qualifier
import javax.inject.Singleton

/**
 * Fase 4 KMP: reemplaza el Retrofit/OkHttp/Gson que había acá por los clientes de Ktor
 * del módulo `shared` (mismo código que va a usar la app de iPhone). Ningún endpoint del
 * backend cambia — es la misma URL base y el mismo header X-User-Id de siempre, solo que
 * ahora la implementación HTTP vive en un solo lugar compartido con iOS.
 */

@Qualifier
@Retention(AnnotationRetention.RUNTIME)
annotation class FallbackHttpClient

@Module
@InstallIn(SingletonComponent::class)
object NetworkModule {

    @Singleton
    @Provides
    fun provideHttpClient(): HttpClient = createEMusicHttpClient()

    @FallbackHttpClient
    @Singleton
    @Provides
    fun provideFallbackHttpClient(): HttpClient = createPipedFallbackHttpClient()

    @Singleton
    @Provides
    fun provideMusicApiClient(
        @ApplicationContext context: Context,
        httpClient: HttpClient
    ): MusicApiClient = MusicApiClient(
        httpClient = httpClient,
        userIdProvider = {
            context.dataStore.data
                .map { it[stringPreferencesKey("user_id")] ?: "" }
                .first()
        }
    )

    @Singleton
    @Provides
    fun providePipedFallbackClient(
        @FallbackHttpClient httpClient: HttpClient
    ): PipedFallbackClient = PipedFallbackClient(httpClient)

    @Singleton
    @Provides
    fun provideEMusicNetworkClient(
        api: MusicApiClient,
        fallback: PipedFallbackClient
    ): EMusicNetworkClient = EMusicNetworkClient(api, fallback)
}
