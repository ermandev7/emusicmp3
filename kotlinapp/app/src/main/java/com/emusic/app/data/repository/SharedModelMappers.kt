package com.emusic.app.data.repository

/**
 * Fase 4 KMP: puente entre los modelos de `shared` (com.emusic.shared.api, usados por
 * MusicApiClient/EMusicNetworkClient con Ktor) y los modelos históricos de la app
 * (com.emusic.app.data.api, usados por toda la UI/ViewModels/MusicService). Los campos
 * son idénticos en ambos lados —`shared` es una copia 1:1 hecha en la Fase 2— así que
 * esto es solo para no tener que tocar ningún consumidor existente de MusicRepository.
 */

fun com.emusic.shared.api.Track.toAppTrack(): com.emusic.app.data.api.Track = com.emusic.app.data.api.Track(
    type = type,
    title = title,
    uploaderName = uploaderName,
    uploader = uploader,
    artist = artist,
    author = author,
    thumbnail = thumbnail,
    thumbnailUrl = thumbnailUrl,
    duration = duration,
    url = url,
    videoIdFromJson = videoIdFromJson,
    id = id
)

fun com.emusic.shared.api.AudioStream.toAppAudioStream(): com.emusic.app.data.api.AudioStream =
    com.emusic.app.data.api.AudioStream(url = url, bitrate = bitrate, quality = quality, mimeType = mimeType)

fun com.emusic.shared.api.StreamInfo.toAppStreamInfo(): com.emusic.app.data.api.StreamInfo = com.emusic.app.data.api.StreamInfo(
    title = title,
    uploader = uploader,
    thumbnailUrl = thumbnailUrl,
    audioStreams = audioStreams.map { it.toAppAudioStream() },
    relatedStreams = relatedStreams.map { it.toAppTrack() }
)

fun com.emusic.shared.api.GenreStat.toAppGenreStat(): com.emusic.app.data.api.GenreStat =
    com.emusic.app.data.api.GenreStat(genre = genre, count = count)

fun com.emusic.shared.api.Playlist.toAppPlaylist(): com.emusic.app.data.api.Playlist = com.emusic.app.data.api.Playlist(
    id = id,
    name = name,
    description = description,
    songsJson = songsJson,
    tracks = tracks.map { it.toAppTrack() }
)

/** Igual que FavoriteEntry.toTrack() de la app: el `id` del endpoint /favorites es el videoId. */
fun com.emusic.shared.api.FavoriteEntry.toAppTrack(): com.emusic.app.data.api.Track = com.emusic.app.data.api.Track(
    title = title,
    artist = artist,
    thumbnailUrl = thumbnailUrl,
    duration = duration,
    videoIdFromJson = id
)

fun com.emusic.shared.api.HistoryEntry.toAppTrack(): com.emusic.app.data.api.Track = com.emusic.app.data.api.Track(
    title = title,
    artist = artist,
    thumbnailUrl = thumbnailUrl,
    duration = duration,
    videoIdFromJson = videoId,
    id = id
)
