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
        // Sin red no se intenta siquiera: antes se lanzaban las tres llamadas igual y la
        // pantalla se quedaba cargando hasta que cada una agotaba su tiempo de espera, una
        // detras de otra. Descargas, que no necesita internet, quedaba inaccesible.
        guard Connectivity.shared.isOnline else {
            errorMessage = nil
            isLoading = false
            return
        }

        isLoading = true
        defer { isLoading = false }

        // En paralelo y cada una por su cuenta: que fallen los favoritos no debe dejar sin
        // historial ni sin playlists. Antes iban en serie dentro del mismo `do`, asi que el
        // primer fallo se llevaba por delante las otras dos.
        async let favs = try? api.getFavorites()
        async let hist = try? api.getHistory()
        async let lists = try? api.getPlaylists()

        let (f, h, l) = await (favs, hist, lists)

        if let f { favorites = f.map { $0.toTrack() } }
        if let h { history = h.map { $0.toTrack() } }
        if let l { playlists = l }

        errorMessage = (f == nil && h == nil && l == nil)
            ? "No se pudo conectar con el servidor."
            : nil
    }

    /// Solo la pestaña visible, igual que `refreshCurrentTab` en Android. Se llama al
    /// cambiar de pestaña y al volver del reproductor (por ejemplo tras marcar un
    /// favorito), para que el cambio se vea sin reiniciar.
    func refreshCurrentTab() async {
        // Descargas es local: nunca depende de la red, y por eso se puede consultar
        // igualmente sin conexion.
        guard tab != .downloads else { return }
        guard Connectivity.shared.isOnline else { return }

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
