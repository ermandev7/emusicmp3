package com.emusic.app.data.api

/**
 * Lógica pura de selección de stream de audio, compartida por la reproducción,
 * el comando de voz de Android Auto y la descarga. Sin dependencias de Android
 * para poder testearla en JVM.
 */

/** Mejor stream de audio: solo MIME de tipo audio, el de mayor bitrate. */
fun StreamInfo.bestAudioStream(): AudioStream? =
    audioStreams
        .filter { it.mimeType.startsWith("audio/") }
        .maxByOrNull { it.bitrate }

/** URL del mejor stream de audio, o null si no hay ninguno válido. */
fun StreamInfo.bestAudioUrl(): String? =
    bestAudioStream()?.url?.takeIf { it.isNotEmpty() }
