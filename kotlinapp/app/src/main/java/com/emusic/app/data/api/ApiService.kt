package com.emusic.app.data.api

import retrofit2.Response
import retrofit2.http.*

interface ApiService {

    @GET("search")
    suspend fun search(@Query("q") query: String): Response<SearchResponse>

    @GET("streams/{videoId}")
    suspend fun getStream(@Path("videoId") videoId: String): Response<StreamInfo>

    @POST("streams/prefetch")
    suspend fun prefetch(@Body request: PrefetchRequest): Response<Unit>

    @GET("trending")
    suspend fun getTrending(): Response<SearchResponse>

    @GET("favorites")
    suspend fun getFavorites(): Response<List<FavoriteEntry>>

    @POST("favorites")
    suspend fun addFavorite(@Body request: AddFavoriteRequest): Response<Unit>

    @DELETE("favorites/{videoId}")
    suspend fun removeFavorite(@Path("videoId") videoId: String): Response<Unit>

    @GET("favorites/{videoId}")
    suspend fun isFavorite(@Path("videoId") videoId: String): Response<Unit>

    @GET("history")
    suspend fun getHistory(): Response<List<HistoryEntry>>

    @GET("history/top-genres")
    suspend fun getTopGenres(): Response<List<GenreStat>>

    @POST("history")
    suspend fun addHistory(@Body request: AddHistoryRequest): Response<Unit>

    @PATCH("history/{videoId}/skip")
    suspend fun markSkipped(@Path("videoId") videoId: String): Response<Unit>

    @GET("recommendation")
    suspend fun getRecommendations(@Query("limit") limit: Int = 20): Response<RecommendationResponse>

    @POST("recommendation/exclude")
    suspend fun excludeRecommendation(@Body request: ExcludeRequest): Response<Unit>

    @GET("playlists")
    suspend fun getPlaylists(): Response<List<Playlist>>

    @POST("playlists")
    suspend fun createPlaylist(@Body request: CreatePlaylistRequest): Response<Playlist>

    @DELETE("playlists/{id}")
    suspend fun deletePlaylist(@Path("id") id: Int): Response<Unit>

    @POST("playlists/{id}/tracks")
    suspend fun addTrackToPlaylist(
        @Path("id") playlistId: Int,
        @Body request: AddToPlaylistRequest
    ): Response<Unit>

    @DELETE("playlists/{id}/tracks/{videoId}")
    suspend fun removeTrackFromPlaylist(
        @Path("id") playlistId: Int,
        @Path("videoId") videoId: String
    ): Response<Unit>
}
