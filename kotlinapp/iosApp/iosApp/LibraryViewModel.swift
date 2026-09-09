import Foundation
import shared

/// Puerto de `ui/library/LibraryViewModel.kt`.
///
/// Diferencia respecto de Android: alli hay una cuarta fuente local, `DownloadsRepository`
/// (archivos descargados en el telefono). En iOS todavia no existe ese subsistema, asi que
/// la pestaña Descargas se muestra vacia. Todo lo demas sale de los mismos endpoints.
enum LibraryTab: CaseIterable, Identifiable {
    case favorites, history, downloads, playlists

    var id: Self { self }

    var title: String {
        switch self {
        case .favorites: return "Favoritos"
        case .history: return "Historial"
        case .downloads: return "Descargas"
        case .playlists: return "Playlists"
        }
    }

    /// Equivalentes SF Symbols de los iconos Material de `LibraryTabChip`.
    var icon: String {
        switch self {
        case .favorites: return "heart.fill"
        case .history: return "clock.arrow.circlepath"
        case .downloads: return "arrow.down.circle"
        case .playlists: return "music.note.list"
        }
    }
}

@MainActor
final class LibraryViewModel: ObservableObject {

    @Published var tab: LibraryTab = .favorites {
        didSet { Task { await refreshCurrentTab() } }
    }

    @Published private(set) var favorites: [Track] = []
    @Published private(set) var history: [Track] = []
    @Published private(set) var playlists: [Playlist] = []
    @Published private(set) var isLoading = false
    @Published private(set) var errorMessage: String?

    private let api = SharedClients.shared.api

    func loadAll() async {
        isLoading = true
        defer { isLoading = false }

        do {
            favorites = try await api.getFavorites().map { $0.toTrack() }
            history = try await api.getHistory().map { $0.toTrack() }
            playlists = try await api.getPlaylists()
            errorMessage = nil
        } catch {
            errorMessage = "No se pudo cargar la biblioteca: \(error.localizedDescription)"
        }
    }

    /// Solo la pestaña visible, igual que `refreshCurrentTab` en Android. Se llama al
    /// cambiar de pestaña y al volver del reproductor (por ejemplo tras marcar un
    /// favorito), para que el cambio se vea sin reiniciar.
    func refreshCurrentTab() async {
        switch tab {
        case .favorites:
            if let list = try? await api.getFavorites() { favorites = list.map { $0.toTrack() } }
        case .history:
            if let list = try? await api.getHistory() { history = list.map { $0.toTrack() } }
        case .playlists:
            if let list = try? await api.getPlaylists() { playlists = list }
        case .downloads:
            break
        }
    }

    // MARK: - Playlists

    func createPlaylist(named name: String) {
        let trimmed = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }
        Task {
            _ = try? await api.createPlaylist(request: CreatePlaylistRequest(name: trimmed))
            if let list = try? await api.getPlaylists() { playlists = list }
        }
    }

    func deletePlaylist(id: Int32) {
        // Se quita ya de la lista para que la UI responda al instante, igual que hace
        // "No me interesa" en Inicio.
        playlists.removeAll { $0.id == id }
        Task {
            try? await api.deletePlaylist(id: id)
            if let list = try? await api.getPlaylists() { playlists = list }
        }
    }

    func removeTrack(_ videoId: String, from playlistId: Int32) {
        Task {
            try? await api.removeTrackFromPlaylist(playlistId: playlistId, videoId: videoId)
            if let list = try? await api.getPlaylists() { playlists = list }
        }
    }
}
