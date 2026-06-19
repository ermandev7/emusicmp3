package com.emusic.app.data.api

import retrofit2.Response
import retrofit2.http.GET
import retrofit2.http.Path
import retrofit2.http.Query
import retrofit2.http.Url

// Llamadas directas a instancias públicas de Piped cuando el backend local no responde
interface FallbackApiService {

    @GET
    suspend fun search(
        @Url baseUrl: String,
        @Query("q") query: String,
        @Query("filter") filter: String = "music_songs"
    ): Response<SearchResponse>

    @GET
    suspend fun getStream(
        @Url baseUrl: String,
        @Path("videoId") videoId: String
    ): Response<StreamInfo>
}

// Retrofit no soporta @Url + @Path juntos, así que la clase concreta construye la URL
class FallbackApiServiceImpl(
    private val retrofitFactory: (String) -> FallbackPipedApi
) : FallbackApiService {

    override suspend fun search(baseUrl: String, query: String, filter: String): Response<SearchResponse> {
        return retrofitFactory(baseUrl).search(query, filter)
    }

    override suspend fun getStream(baseUrl: String, videoId: String): Response<StreamInfo> {
        return retrofitFactory(baseUrl).getStream(videoId)
    }
}

interface FallbackPipedApi {
    @GET("search")
    suspend fun search(
        @Query("q") query: String,
        @Query("filter") filter: String = "music_songs"
    ): Response<SearchResponse>

    @GET("streams/{videoId}")
    suspend fun getStream(@Path("videoId") videoId: String): Response<StreamInfo>
}
