import Foundation
import shared

/// Seleccion del stream de audio a reproducir.
///
/// **Ojo: esto NO es una copia literal de `app/data/api/StreamSelection.kt`, y es a proposito.**
///
/// Android elige simplemente el de mayor bitrate entre los MIME `audio/`. En YouTube ese
/// suele ser **Opus dentro de WebM** (~160 kbps), que ExoPlayer decodifica sin problema.
/// AVFoundation, en cambio, **no soporta WebM ni Opus**: el `AVPlayerItem` nunca llega a
/// `readyToPlay` y la reproduccion se queda colgada sin error claro.
///
/// Por eso en iOS se prioriza por contenedor y recien despues por bitrate:
///   1. `audio/mp4` (AAC) — el formato nativo de Apple, es el que YouTube sirve como m4a.
///   2. `audio/mpeg` (MP3) y `audio/aac` sueltos, por si alguna instancia de Piped los da.
///   3. Cualquier otro, como ultimo recurso: probablemente falle, pero es mejor intentar
///      que no reproducir nada.
extension StreamInfo {

    /// MIME types que AVFoundation puede decodificar, en orden de preferencia.
    private static let playablePrefixes = ["audio/mp4", "audio/aac", "audio/mpeg", "audio/x-m4a"]

    /// Mejor stream reproducible en iOS: primero por compatibilidad, luego por bitrate.
    var bestAudioStream: AudioStream? {
        let audioOnly = audioStreams.filter { $0.mimeType.hasPrefix("audio/") && !$0.url.isEmpty }
        guard !audioOnly.isEmpty else { return nil }

        for prefix in Self.playablePrefixes {
            let matching = audioOnly.filter { $0.mimeType.hasPrefix(prefix) }
            if let best = matching.max(by: { $0.bitrate < $1.bitrate }) {
                return best
            }
        }

        // Nada compatible: se devuelve el de mayor bitrate igual, para que el error
        // que muestre AVPlayer sea explicito en vez de un silencio.
        return audioOnly.max { $0.bitrate < $1.bitrate }
    }

    var bestAudioURL: URL? {
        guard let raw = bestAudioStream?.url, !raw.isEmpty else { return nil }
        return URL(string: raw)
    }

    /// Resumen de los formatos disponibles, para diagnosticar cuando algo no suena.
    var audioStreamsDescription: String {
        audioStreams
            .map { "\($0.mimeType)@\($0.bitrate)" }
            .joined(separator: ", ")
    }
}
