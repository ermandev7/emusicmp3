package com.emusic.app.di

import android.content.Context
import androidx.datastore.preferences.core.stringPreferencesKey
import com.emusic.app.data.api.ApiService
import com.emusic.app.data.api.FallbackApiService
import com.emusic.app.data.api.FallbackApiServiceImpl
import com.emusic.app.data.api.FallbackPipedApi
import com.emusic.app.ui.setup.dataStore
import dagger.Module
import dagger.Provides
import dagger.hilt.InstallIn
import dagger.hilt.android.qualifiers.ApplicationContext
import dagger.hilt.components.SingletonComponent
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.runBlocking
import okhttp3.Interceptor
import okhttp3.OkHttpClient
import okhttp3.logging.HttpLoggingInterceptor
import retrofit2.Retrofit
import retrofit2.converter.gson.GsonConverterFactory
import java.util.concurrent.TimeUnit
import javax.inject.Singleton

private const val BASE_URL = "http://emusicmp3.duckdns.org:5050/api/"

@Module
@InstallIn(SingletonComponent::class)
object NetworkModule {

    @Singleton
    @Provides
    fun provideOkHttpClient(@ApplicationContext context: Context): OkHttpClient {
        val userIdInterceptor = Interceptor { chain ->
            val userId = runBlocking {
                context.dataStore.data
                    .map { it[stringPreferencesKey("user_id")] ?: "" }
                    .first()
            }
            val request = chain.request().newBuilder()
                .addHeader("X-User-Id", userId)
                .build()
            chain.proceed(request)
        }
        return OkHttpClient.Builder()
            .connectTimeout(10, TimeUnit.SECONDS)
            .readTimeout(25, TimeUnit.SECONDS)
            .addInterceptor(userIdInterceptor)
            .addInterceptor(HttpLoggingInterceptor().apply {
                level = HttpLoggingInterceptor.Level.BASIC
            })
            .build()
    }

    @Singleton
    @Provides
    fun provideRetrofit(client: OkHttpClient): Retrofit =
        Retrofit.Builder()
            .baseUrl(BASE_URL)
            .client(client)
            .addConverterFactory(GsonConverterFactory.create())
            .build()

    @Singleton
    @Provides
    fun provideApiService(retrofit: Retrofit): ApiService =
        retrofit.create(ApiService::class.java)

    @Singleton
    @Provides
    fun provideFallbackApiService(client: OkHttpClient): FallbackApiService {
        return FallbackApiServiceImpl { baseUrl ->
            Retrofit.Builder()
                .baseUrl("$baseUrl/")
                .client(
                    client.newBuilder()
                        .connectTimeout(7, TimeUnit.SECONDS)
                        .readTimeout(7, TimeUnit.SECONDS)
                        .build()
                )
                .addConverterFactory(GsonConverterFactory.create())
                .build()
                .create(FallbackPipedApi::class.java)
        }
    }
}
