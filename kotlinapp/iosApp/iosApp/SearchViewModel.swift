import Foundation
import shared

/// Puerto de `ui/search/SearchViewModel.kt`: busca contra `EMusicNetworkClient` del modulo
/// compartido, o sea exactamente la misma ruta que usa Android (backend propio y, si
/// falla, las instancias publicas de Piped), y guarda las busquedas recientes.
///
/// Los metodos `suspend` de Kotlin llegan a Swift como `async throws`, asi que se
/// pueden usar con `await` sin ningun puente extra.
@MainActor
final class SearchViewModel: ObservableObject {

    @Published var query: String = ""
    @Published private(set) var tracks: [Track] = []
    @Published private(set) var isLoading: Bool = false
    @Published private(set) var statusMessage: String?

    /// Igual que en Android: hasta que no se busca una vez se muestran las recientes, no un
    /// "sin resultados" que todavia no significa nada.
    @Published private(set) var hasSearched = false

    @Published private(set) var recentSearches: [String] = []

    private let network = SharedClients.shared.network
    private let api = SharedClients.shared.api

    /// El DataStore de Android guarda las recientes como un string con saltos de linea y un
    /// tope de 10. Aca es UserDefaults, pero mismo tope y mismo orden: la ultima buscada
    /// primero, sin repetidas.
    private static let recentKey = "recent_searches"
    private static let maxRecent = 10

    init() {
        recentSearches = Self.loadRecent()
    }

    func search() async {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }

        isLoading = true
        hasSearched = true
        statusMessage = nil
        defer { isLoading = false }

        saveRecent(trimmed)

        do {
            let results = try await network.search(query: trimmed)
            tracks = results
            statusMessage = results.isEmpty ? "Sin resultados para «\(trimmed)»" : nil
            prefetchTop(results)
        } catch {
            tracks = []
            statusMessage = "Error de red: \(error.localizedDescription)"
        }
    }

    /// Tocar una busqueda reciente la reejecuta, como `searchQuery` en Android.
    func search(_ text: String) async {
        query = text
        await search()
    }

    func clear() {
        query = ""
        tracks = []
        statusMessage = nil
        hasSearched = false
    }

    // MARK: - Recientes

    private static func loadRecent() -> [String] {
        (UserDefaults.standard.string(forKey: recentKey) ?? "")
            .split(separator: "\n")
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .filter { !$0.isEmpty }
    }

    private func persistRecent() {
        UserDefaults.standard.set(recentSearches.joined(separator: "\n"), forKey: Self.recentKey)
    }

    private func saveRecent(_ text: String) {
        // Sin distinguir mayusculas, igual que `equals(query, ignoreCase = true)` en Android:
        // buscar "Jerome" despues de "jerome" no debe dejar dos entradas.
        var updated = recentSearches.filter { $0.caseInsensitiveCompare(text) != .orderedSame }
        updated.insert(text, at: 0)
        recentSearches = Array(updated.prefix(Self.maxRecent))
        persistRecent()
    }

    func removeRecent(_ text: String) {
        recentSearches.removeAll { $0.caseInsensitiveCompare(text) == .orderedSame }
        persistRecent()
    }

    // MARK: - Prefetch

    /// Le pide al backend que vaya resolviendo los 3 primeros streams, igual que
    /// `SearchViewModel.kt` en Android. Sin esto, el primer play sale en frio: la Pi
    /// tiene que lanzar yt-dlp en ese momento y tarda entre 5 y 15 segundos. Con la
    /// precarga el `getStream` posterior sale de la cache de 50 min del servidor.
    ///
    /// No se espera el resultado: es fuego y olvido, y si falla no pasa nada.
    private func prefetchTop(_ results: [Track]) {
        let ids = results.prefix(3).map(\.videoId).filter { !$0.isEmpty }
        guard !ids.isEmpty else { return }
        Task { try? await api.prefetch(videoIds: ids) }
    }
}

/// `mm:ss` a partir de los segundos que devuelve el backend.
func formatDuration(_ seconds: Int32) -> String {
    guard seconds > 0 else { return "--:--" }
    let total = Int(seconds)
    return String(format: "%d:%02d", total / 60, total % 60)
}
