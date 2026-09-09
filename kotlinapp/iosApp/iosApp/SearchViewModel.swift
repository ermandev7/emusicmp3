import Foundation
import shared

/// Primera pantalla real de la app iOS: busca contra `EMusicNetworkClient` del modulo
/// compartido, o sea exactamente la misma ruta que usa Android (backend propio y, si
/// falla, las instancias publicas de Piped).
///
/// Los metodos `suspend` de Kotlin llegan a Swift como `async throws`, asi que se
/// pueden usar con `await` sin ningun puente extra.
@MainActor
final class SearchViewModel: ObservableObject {

    @Published var query: String = ""
    @Published var tracks: [Track] = []
    @Published var isLoading: Bool = false
    @Published var statusMessage: String?

    private let network = SharedClients.shared.network
    private let api = SharedClients.shared.api

    func search() async {
        let trimmed = query.trimmingCharacters(in: .whitespacesAndNewlines)
        guard !trimmed.isEmpty else { return }

        isLoading = true
        statusMessage = nil
        defer { isLoading = false }

        do {
            let results = try await network.search(query: trimmed)
            tracks = results
            statusMessage = results.isEmpty ? "Sin resultados para \"\(trimmed)\"" : nil
            prefetchTop(results)
        } catch {
            tracks = []
            statusMessage = "Error de red: \(error.localizedDescription)"
        }
    }

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
