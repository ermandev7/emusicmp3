import Foundation
import shared

/// Puerto de `ui/home/HomeViewModel.kt`. Carga las tres secciones del Inicio en paralelo
/// contra los mismos endpoints que Android, a traves del `MusicApiClient` compartido.
@MainActor
final class HomeViewModel: ObservableObject {

    @Published private(set) var mostPlayed: [Track] = []
    @Published private(set) var recommendations: [Track] = []
    @Published private(set) var topGenres: [GenreStat] = []
    @Published private(set) var isLoading = false
    @Published private(set) var isLoadingReco = false

    /// Fallo de red al cargar Inicio. Se pinta bajo la cabecera.
    @Published private(set) var errorMessage: String?

    /// Nombre del genero cuya busqueda esta en curso, para poner esa tarjeta en "Cargando...".
    @Published private(set) var loadingGenre: String?

    private let api = SharedClients.shared.api
    private let network = SharedClients.shared.network

    /// Cuantas recomendaciones mostrar. Con el tope de 2 por artista que aplica el backend,
    /// 12 garantiza al menos 6 artistas distintos.
    private let recommendationLimit: Int32 = 12

    var isEmpty: Bool {
        !isLoading && !isLoadingReco && mostPlayed.isEmpty && recommendations.isEmpty
    }

    func loadAll() async {
        async let historyAndGenres: Void = loadHistoryAndGenres()
        async let reco: Void = loadRecommendations()
        _ = await (historyAndGenres, reco)
    }

    private func loadHistoryAndGenres() async {
        isLoading = true
        defer { isLoading = false }

        // "Mas escuchadas" no es un endpoint propio: es el historial ordenado por
        // playCount, igual que hace MusicRepository.getMostPlayed en Android.
        //
        // Los errores NO se tragan en silencio: una lista vacia por un fallo de red se
        // ve igual que una lista vacia de verdad, y eso ya costo una tarde de depuracion.
        do {
            let history = try await api.getHistory()
            mostPlayed = history
                .sorted { $0.playCount > $1.playCount }
                .prefix(20)
                .map { $0.toTrack() }
            errorMessage = nil
        } catch {
            errorMessage = "No se pudo conectar con el servidor: \(error.localizedDescription)"
        }

        if let genres = try? await api.getTopGenres() {
            topGenres = genres
        }
    }

    private func loadRecommendations() async {
        isLoadingReco = true
        defer { isLoadingReco = false }

        if let response = try? await api.getRecommendations(limit: recommendationLimit) {
            recommendations = response.items
        }
    }

    /// Tocar un genero dispara la MISMA busqueda que usa el buscador y arranca en modo
    /// radio con el primer resultado — no encola la lista cruda (ver RadioEngine).
    /// Devuelve true si arrancó a sonar, para que la pantalla abra el reproductor
    /// (el `onTrackClick` de `HomeScreen.kt`, que navega a Screen.Player).
    @discardableResult
    func playGenre(_ genre: EMusicGenre, player: PlayerEngine) async -> Bool {
        loadingGenre = genre.name
        defer { loadingGenre = nil }

        guard let results = try? await network.search(query: genre.query),
              let first = results.first else { return false }

        // Nada de prefetch aca. `HomeViewModel.searchGenre` en Android tampoco lo hace, y
        // por una razon: el backend NO deduplica peticiones en vuelo, asi que precargar el
        // mismo videoId que el reproductor esta a punto de pedir lanza yt-dlp DOS VECES en
        // paralelo en la Pi y el arranque tarda el doble. El prefetch tiene sentido cuando
        // hay un hueco antes del play (el buscador, o los siguientes de la cola).
        player.play(track: first, radioSeed: true)
        return true
    }

    /// "No me interesa": lo saca de la lista al instante y lo excluye en el backend.
    func exclude(_ track: Track) {
        recommendations.removeAll { $0.videoId == track.videoId }
        Task {
            try? await api.excludeRecommendation(
                request: ExcludeRequest(
                    videoId: track.videoId,
                    artist: track.displayArtist,
                    title: track.title
                )
            )
        }
    }
}
