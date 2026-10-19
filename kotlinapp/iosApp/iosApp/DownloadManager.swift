import Foundation
import shared

/// Estado de la descarga en curso. Una a la vez, igual que en Android: es una app de uso
/// personal contra una Raspberry Pi, y varias descargas en paralelo solo servirían para
/// pelearse por el ancho de banda con la canción que está sonando.
struct DownloadState: Equatable {
    var videoId: String?
    var title: String = ""
    var artist: String = ""
    var thumbnailUrl: String = ""
    var isDownloading: Bool = false
    /// 0...100. Vale -1 mientras el servidor no diga cuánto pesa el archivo.
    var progress: Int = 0
    var errorMessage: String?
}

/// Puerto de `data/download/DownloadManager.kt`.
///
/// Como allí, vive **fuera del ciclo de vida de las pantallas**: es un singleton, no un
/// `@StateObject` de una vista. El comentario de Android explica por qué, y vale igual
/// aquí: cuando la descarga corría dentro de la ViewModel de la pantalla, cambiar de
/// pantalla destruía la ViewModel, cancelaba la corrutina y borraba el archivo a medias.
@MainActor
final class DownloadManager: NSObject, ObservableObject {

    static let shared = DownloadManager()

    @Published private(set) var state = DownloadState()

    private let store = DownloadStore.shared
    private let network = SharedClients.shared.network
    private let api = SharedClients.shared.api

    private var session: URLSession!
    private var task: URLSessionDownloadTask?

    /// Datos del tema que se está bajando, para poder guardarlo cuando termine.
    private var pending: (track: Track, fileName: String)?

    private override init() {
        super.init()
        // Delegado propio para poder informar del progreso; la cola es la principal
        // porque todo lo que hacemos al recibirlo toca estado publicado.
        session = URLSession(configuration: .default, delegate: self, delegateQueue: .main)
    }

    func isDownloading(_ videoId: String) -> Bool {
        state.isDownloading && state.videoId == videoId
    }

    /// Empieza a descargar. Si ya hay una en curso no hace nada, como en Android.
    func download(_ track: Track) {
        guard !state.isDownloading else { return }
        guard !track.videoId.isEmpty else { return }
        guard !store.isDownloaded(track.videoId) else { return }

        state = DownloadState(
            videoId: track.videoId,
            title: track.title,
            artist: track.displayArtist,
            thumbnailUrl: track.displayThumbnail,
            isDownloading: true,
            progress: -1
        )

        Task { await start(track) }
    }

    func cancel() {
        task?.cancel()
        task = nil
        pending = nil
        state = DownloadState()
    }

    private func start(_ track: Track) async {
        // Se descarga EL MISMO stream que se reproduciría: `bestAudioStream` prioriza los
        // contenedores que AVFoundation sabe decodificar. Bajar el de mayor bitrate a secas,
        // como hace Android, traería el Opus/WebM — y entonces el archivo descargado no se
        // podría reproducir, que es exactamente el bug que ya nos costó una tarde.
        guard let info = try? await network.getStream(videoId: track.videoId),
              let stream = info.bestAudioStream,
              let url = URL(string: stream.url), !stream.url.isEmpty
        else {
            fail("No se pudo obtener el audio de «\(track.title)».")
            return
        }

        pending = (track, store.fileName(for: track.videoId, mimeType: stream.mimeType))

        // googlevideo rechaza las peticiones sin User-Agent de navegador, igual que en
        // Android (por eso allí usan HttpURLConnection a mano y no el cliente normal).
        var request = URLRequest(url: url)
        request.setValue(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 16_0 like Mac OS X) eMusic/1.0",
            forHTTPHeaderField: "User-Agent"
        )
        request.timeoutInterval = 30

        task = session.downloadTask(with: request)
        task?.resume()
    }

    private func fail(_ message: String) {
        task = nil
        pending = nil
        state = DownloadState(errorMessage: message)
    }

    /// Mueve el archivo temporal a su sitio y lo registra. Se llama desde el delegado.
    fileprivate func finish(tempURL: URL, response: URLResponse?) {
        guard let pending else { return }

        if let http = response as? HTTPURLResponse, !(200...299).contains(http.statusCode) {
            fail("El servidor respondió \(http.statusCode) al descargar «\(pending.track.title)».")
            return
        }

        let destination = store.fileURL(named: pending.fileName)
        do {
            if FileManager.default.fileExists(atPath: destination.path) {
                try FileManager.default.removeItem(at: destination)
            }
            // Mover, no copiar: el archivo temporal de URLSession se borra en cuanto
            // vuelve el delegado, así que copiar sería trabajo (y disco) de más.
            try FileManager.default.moveItem(at: tempURL, to: destination)
        } catch {
            fail("No se pudo guardar «\(pending.track.title)»: \(error.localizedDescription)")
            return
        }

        let track = pending.track

        // La carátula se guarda en calidad alta y construida desde el videoId, no la
        // miniatura del listado —que es un proxy pequeño y se vería pixelada a pantalla
        // completa—. Mismo criterio que el comentario de Android.
        let thumb = track.sddThumbnail.isEmpty ? track.displayThumbnail : track.sddThumbnail

        store.add(DownloadedTrack(
            videoId: track.videoId,
            title: track.title,
            artist: track.displayArtist,
            thumbnailUrl: thumb,
            duration: track.duration,
            fileName: pending.fileName
        ))

        // El historial marca el tema como descargado, y eso pesa en el recomendador:
        // `BuildProfile` le aplica un bonus de 1.5x. Descargar algo es una señal fuerte.
        Task {
            try? await api.addHistory(
                request: AddHistoryRequest(
                    title: track.title,
                    artist: track.displayArtist,
                    thumbnailUrl: track.displayThumbnail,
                    duration: track.duration,
                    videoId: track.videoId,
                    isDownloaded: true
                )
            )
        }

        self.pending = nil
        self.task = nil
        state = DownloadState()
    }
}

// MARK: - Progreso

extension DownloadManager: URLSessionDownloadDelegate {

    nonisolated func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didWriteData bytesWritten: Int64,
        totalBytesWritten: Int64,
        totalBytesExpectedToWrite: Int64
    ) {
        // -1 cuando el servidor no manda Content-Length: la UI lo pinta indeterminado
        // en vez de fingir un porcentaje.
        let pct = totalBytesExpectedToWrite > 0
            ? Int(min(100, max(0, totalBytesWritten * 100 / totalBytesExpectedToWrite)))
            : -1

        Task { @MainActor in
            guard self.state.isDownloading else { return }
            self.state.progress = pct
        }
    }

    nonisolated func urlSession(
        _ session: URLSession,
        downloadTask: URLSessionDownloadTask,
        didFinishDownloadingTo location: URL
    ) {
        // El archivo temporal desaparece en cuanto vuelve este método, así que hay que
        // moverlo AQUÍ, de forma síncrona, y no en una tarea posterior.
        let response = downloadTask.response
        let fm = FileManager.default
        let safeCopy = fm.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        try? fm.moveItem(at: location, to: safeCopy)

        Task { @MainActor in
            self.finish(tempURL: safeCopy, response: response)
        }
    }

    nonisolated func urlSession(
        _ session: URLSession,
        task: URLSessionTask,
        didCompleteWithError error: Error?
    ) {
        guard let error else { return }
        let cancelled = (error as NSError).code == NSURLErrorCancelled
        Task { @MainActor in
            if cancelled {
                self.state = DownloadState()
            } else {
                self.fail("Falló la descarga: \(error.localizedDescription)")
            }
        }
    }
}
