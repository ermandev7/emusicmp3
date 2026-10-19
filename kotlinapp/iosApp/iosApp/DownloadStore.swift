import Foundation
import shared

/// Una canción descargada al dispositivo.
struct DownloadedTrack: Identifiable, Codable, Equatable {
    let videoId: String
    let title: String
    let artist: String
    let thumbnailUrl: String
    let duration: Int32
    /// Nombre del archivo dentro de la carpeta de descargas, con su extensión.
    let fileName: String

    var id: String { videoId }

    /// Vuelve a `Track` para poder encolarlo en el reproductor como cualquier otro tema.
    func toTrack() -> Track {
        Track(
            type: "stream",
            title: title,
            uploaderName: artist,
            uploader: nil,
            artist: artist,
            author: nil,
            thumbnail: thumbnailUrl,
            thumbnailUrl: thumbnailUrl,
            duration: duration,
            url: "",
            videoIdFromJson: videoId,
            id: 0
        )
    }
}

/// Dónde viven los archivos descargados y qué sabemos de ellos.
///
/// **Bastante más simple que en Android, y no por casualidad.** Allí hay que pasar por
/// MediaStore: elegir entre la carpeta de Música o la de Descargas según el MIME (porque
/// `MediaStore.Audio` rechaza webm), escribir con `IS_PENDING` para que sea atómico, y pedir
/// permiso al sistema para borrar archivos que no creó esa instalación. En iOS los archivos
/// de la app son suyos y de nadie más, así que basta con una carpeta y un JSON al lado — y
/// `DownloadTarget.kt` y el flujo de consentimiento de borrado no tienen equivalente aquí.
///
/// La otra diferencia, esta a favor de iOS: los archivos se nombran por **videoId**, no por
/// el título. Android sanea el título y lo usa de nombre, lo que obliga a guardar los
/// metadatos indexados por la URI de MediaStore y hace imposible saber si un tema concreto
/// ya está descargado. Con el videoId, comprobarlo es inmediato — que es justo lo que
/// necesita el reproductor para preferir el archivo local antes de salir a la red.
@MainActor
final class DownloadStore: ObservableObject {

    static let shared = DownloadStore()

    @Published private(set) var downloads: [DownloadedTrack] = []

    private let folder: URL
    private let indexURL: URL

    private init() {
        let base = FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
        folder = base.appendingPathComponent("Downloads", isDirectory: true)
        indexURL = folder.appendingPathComponent("index.json")

        createFolderIfNeeded()
        downloads = loadIndex()
    }

    private func createFolderIfNeeded() {
        guard !FileManager.default.fileExists(atPath: folder.path) else { return }
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true)

        // La música descargada no debe subir a iCloud: son archivos grandes, recuperables
        // y personales. Sin esto, el backup del usuario se llena de audio.
        var url = folder
        var values = URLResourceValues()
        values.isExcludedFromBackup = true
        try? url.setResourceValues(values)
    }

    // MARK: - Consultas

    func isDownloaded(_ videoId: String) -> Bool {
        localURL(for: videoId) != nil
    }

    /// Ruta del archivo local de ese tema, o nil si no está descargado.
    ///
    /// Comprueba que el archivo exista de verdad, no solo que figure en el índice: si el
    /// usuario reinstala la app o el sistema limpia el directorio, el índice se queda
    /// desfasado y devolver una ruta muerta rompería la reproducción.
    func localURL(for videoId: String) -> URL? {
        guard let entry = downloads.first(where: { $0.videoId == videoId }) else { return nil }
        let url = folder.appendingPathComponent(entry.fileName)
        return FileManager.default.fileExists(atPath: url.path) ? url : nil
    }

    func fileURL(named fileName: String) -> URL {
        folder.appendingPathComponent(fileName)
    }

    /// Nombre de archivo para un tema, deducido del MIME del stream. Solo se descargan
    /// formatos que AVFoundation sabe decodificar (ver `StreamSelection.swift`), así que
    /// en la práctica esto es casi siempre m4a.
    func fileName(for videoId: String, mimeType: String) -> String {
        let ext: String
        switch true {
        case mimeType.contains("mp4"), mimeType.contains("m4a"): ext = "m4a"
        case mimeType.contains("mpeg"): ext = "mp3"
        case mimeType.contains("aac"): ext = "aac"
        case mimeType.contains("webm"): ext = "webm"
        case mimeType.contains("ogg"), mimeType.contains("opus"): ext = "ogg"
        default: ext = "m4a"
        }
        return "\(videoId).\(ext)"
    }

    // MARK: - Altas y bajas

    func add(_ track: DownloadedTrack) {
        downloads.removeAll { $0.videoId == track.videoId }
        downloads.insert(track, at: 0)
        saveIndex()
    }

    /// Borra el archivo y su entrada. Devuelve false solo si el archivo existía y no se
    /// pudo borrar; una entrada huérfana se limpia igual.
    @discardableResult
    func remove(_ videoId: String) -> Bool {
        guard let entry = downloads.first(where: { $0.videoId == videoId }) else { return true }
        let url = folder.appendingPathComponent(entry.fileName)

        var ok = true
        if FileManager.default.fileExists(atPath: url.path) {
            do { try FileManager.default.removeItem(at: url) }
            catch { ok = false }
        }

        if ok {
            downloads.removeAll { $0.videoId == videoId }
            saveIndex()
        }
        return ok
    }

    // MARK: - Índice en disco

    private func loadIndex() -> [DownloadedTrack] {
        guard let data = try? Data(contentsOf: indexURL),
              let list = try? JSONDecoder().decode([DownloadedTrack].self, from: data)
        else { return [] }

        // Se descartan las entradas cuyo archivo ya no existe, para que la lista nunca
        // muestre algo que no se puede reproducir.
        return list.filter {
            FileManager.default.fileExists(atPath: folder.appendingPathComponent($0.fileName).path)
        }
    }

    private func saveIndex() {
        guard let data = try? JSONEncoder().encode(downloads) else { return }
        try? data.write(to: indexURL, options: .atomic)
    }
}
