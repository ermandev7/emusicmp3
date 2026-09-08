import SwiftUI
import shared

/// Estructura espejo de `AppNavigation.kt`: tres secciones abajo, con el mini reproductor
/// flotando encima de la barra en todas las pantallas (igual que en Android).
struct ContentView: View {

    @StateObject private var player = PlayerEngine.shared

    /// Alto estandar de la barra de tabs de iOS. El mini reproductor se apoya justo encima.
    private let tabBarHeight: CGFloat = 49

    var body: some View {
        ZStack(alignment: .bottom) {
            TabView {
                PendingScreen(
                    title: "Inicio",
                    detail: "Géneros, más escuchadas y recomendadas.\nSe porta desde HomeScreen.kt."
                )
                .tabItem { Label("Inicio", systemImage: "house.fill") }

                SearchScreen(player: player)
                    .tabItem { Label("Buscar", systemImage: "magnifyingglass") }

                PendingScreen(
                    title: "Biblioteca",
                    detail: "Favoritos, historial, descargas y playlists.\nSe porta desde LibraryScreen.kt."
                )
                .tabItem { Label("Biblioteca", systemImage: "music.note.list") }
            }
            .tint(EMusicColor.primary)

            MiniPlayerView(player: player)
                .padding(.bottom, tabBarHeight)
        }
        .animation(.easeInOut(duration: 0.2), value: player.currentTrack?.videoId)
    }
}

// MARK: - Buscar

struct SearchScreen: View {

    @ObservedObject var player: PlayerEngine
    @StateObject private var viewModel = SearchViewModel()

    var body: some View {
        ZStack {
            EMusicColor.background.ignoresSafeArea()

            VStack(spacing: 0) {
                searchField

                if viewModel.isLoading {
                    ProgressView()
                        .tint(EMusicColor.primary)
                        .padding(.top, 32)
                }

                if let message = viewModel.statusMessage {
                    Text(message)
                        .font(.callout)
                        .foregroundStyle(EMusicColor.onSurfaceVariant)
                        .multilineTextAlignment(.center)
                        .padding(24)
                }

                if let error = player.errorMessage {
                    Text(error)
                        .font(.footnote)
                        .foregroundStyle(EMusicColor.favorite)
                        .multilineTextAlignment(.center)
                        .padding(.horizontal, 24)
                        .padding(.bottom, 8)
                }

                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(viewModel.tracks, id: \.videoId) { track in
                            Button {
                                // Tocar un resultado de busqueda activa el modo radio, igual
                                // que en Android: se encola solo esta y `RadioEngine` la extiende.
                                player.play(track: track, radioSeed: true)
                            } label: {
                                TrackRow(
                                    track: track,
                                    isPlaying: player.currentTrack?.videoId == track.videoId
                                )
                            }
                            .buttonStyle(.plain)
                        }
                    }
                    // Espacio para que el mini reproductor no tape la ultima fila.
                    Color.clear.frame(height: EMusicMetrics.bottomContentInset)
                }
            }
        }
    }

    private var searchField: some View {
        HStack(spacing: 10) {
            Image(systemName: "magnifyingglass")
                .foregroundStyle(EMusicColor.onSurfaceVariant)

            TextField("", text: $viewModel.query, prompt:
                Text("Buscar canciones o artistas")
                    .foregroundColor(EMusicColor.onSurfaceVariant)
            )
            .foregroundStyle(EMusicColor.onSurface)
            .textInputAutocapitalization(.never)
            .autocorrectionDisabled()
            .submitLabel(.search)
            .onSubmit { Task { await viewModel.search() } }

            if !viewModel.query.isEmpty {
                Button {
                    viewModel.query = ""
                    viewModel.tracks = []
                    viewModel.statusMessage = nil
                } label: {
                    Image(systemName: "xmark")
                        .foregroundStyle(EMusicColor.onSurfaceVariant)
                }
            }
        }
        .padding(.horizontal, 14)
        .padding(.vertical, 12)
        .background(EMusicColor.surfaceVariant)
        .clipShape(RoundedRectangle(cornerRadius: 24))
        .padding(.horizontal, 12)
        .padding(.top, 8)
        .padding(.bottom, 12)
    }
}

// MARK: - Fila de pista

/// Puerto de `TrackItem.kt`. El titulo se pinta verde cuando ese tema esta sonando.
private struct TrackRow: View {

    let track: Track
    var isPlaying: Bool = false

    var body: some View {
        HStack(spacing: EMusicMetrics.trackRowSpacing) {
            AsyncImage(url: URL(string: track.sdThumbnail)) { image in
                image.resizable().aspectRatio(contentMode: .fill)
            } placeholder: {
                Rectangle().fill(EMusicColor.surfaceVariant)
            }
            .frame(
                width: EMusicMetrics.trackThumbnailSize,
                height: EMusicMetrics.trackThumbnailSize
            )
            .clipShape(RoundedRectangle(cornerRadius: EMusicMetrics.trackThumbnailRadius))

            VStack(alignment: .leading, spacing: 2) {
                Text(track.title)
                    .font(.body)
                    .foregroundStyle(isPlaying ? EMusicColor.primary : EMusicColor.onSurface)
                    .lineLimit(1)
                Text(track.displayArtist)
                    .font(.caption)
                    .foregroundStyle(EMusicColor.onSurfaceVariant)
                    .lineLimit(1)
            }

            Spacer(minLength: 8)

            Text(formatDuration(track.duration))
                .font(.caption.monospacedDigit())
                .foregroundStyle(EMusicColor.onSurfaceVariant)
        }
        .padding(.horizontal, EMusicMetrics.trackRowHorizontalPadding)
        .padding(.vertical, EMusicMetrics.trackRowVerticalPadding)
        .contentShape(Rectangle())
    }
}

// MARK: - Auxiliares

private struct PendingScreen: View {

    let title: String
    let detail: String

    var body: some View {
        ZStack {
            EMusicColor.background.ignoresSafeArea()

            VStack(spacing: 12) {
                Text(title)
                    .font(.title2.bold())
                    .foregroundStyle(EMusicColor.onSurface)
                Text(detail)
                    .font(.footnote)
                    .foregroundStyle(EMusicColor.onSurfaceVariant)
                    .multilineTextAlignment(.center)
            }
            .padding()
        }
    }
}

#Preview {
    ContentView().preferredColorScheme(.dark)
}
