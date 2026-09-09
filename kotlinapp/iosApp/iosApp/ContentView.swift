import SwiftUI
import shared

/// Estructura espejo de `AppNavigation.kt`: las mismas tres secciones, con el reproductor
/// minimizado presente en todas las pantallas.
///
/// La forma SI se aparta de Android a proposito: alli la barra de navegacion va pegada al
/// borde y el mini reproductor es una barra encima. Aca las dos son burbujas flotantes
/// gemelas. Para conseguirlo se oculta la barra de tabs nativa y se dibuja la propia; el
/// `TabView` se mantiene porque es lo que conserva vivo el estado de cada pestaña.
struct ContentView: View {

    @StateObject private var player = PlayerEngine.shared

    /// Arranca en Inicio, igual que el startDestination de AppNavigation.kt.
    @State private var selectedTab = 0

    /// Reproductor completo, abierto desde el mini reproductor.
    @State private var showPlayer = false

    var body: some View {
        ZStack(alignment: .bottom) {
            TabView(selection: $selectedTab) {
                HomeScreen(
                    player: player,
                    onSearchTapped: { selectedTab = 1 },
                    onTrackOpened: { showPlayer = true }
                )
                .toolbar(.hidden, for: .tabBar)
                .tabItem { Label("Inicio", systemImage: "house.fill") }
                .tag(0)

                SearchScreen(player: player, onTrackOpened: { showPlayer = true })
                    .toolbar(.hidden, for: .tabBar)
                    .tabItem { Label("Buscar", systemImage: "magnifyingglass") }
                    .tag(1)

                LibraryScreen(player: player, onTrackOpened: { showPlayer = true })
                    .toolbar(.hidden, for: .tabBar)
                    .tabItem { Label("Biblioteca", systemImage: "music.note.list") }
                    .tag(2)
            }
            .tint(EMusicColor.primary)

            // Las dos burbujas, una encima de otra y con los mismos margenes.
            VStack(spacing: EMusicMetrics.bubbleGap) {
                MiniPlayerView(player: player, onExpand: { showPlayer = true })
                BottomNavBubble(selectedTab: $selectedTab)
            }
            .padding(.horizontal, EMusicMetrics.bubbleInset)
            .padding(.bottom, EMusicMetrics.bubbleBottomPadding)
            // Al flotar, la lista se ve por las rendijas: por el hueco entre las dos
            // burbujas, por los lados y por debajo. Este degradado hace que el contenido
            // se desvanezca en el fondo antes de llegar ahi, en vez de asomar cortado.
            // No intercepta toques: los de las burbujas tienen que seguir llegando.
            .background(
                LinearGradient(
                    colors: [
                        EMusicColor.background.opacity(0),
                        EMusicColor.background.opacity(0.75),
                        EMusicColor.background,
                        EMusicColor.background,
                    ],
                    startPoint: .top,
                    endPoint: .bottom
                )
                .padding(.top, -36)
                .ignoresSafeArea(edges: .bottom)
                .allowsHitTesting(false)
            )
        }
        .animation(.easeInOut(duration: 0.2), value: player.currentTrack?.videoId)
        .fullScreenCover(isPresented: $showPlayer) {
            PlayerScreen(player: player)
        }
    }
}

// MARK: - Buscar

struct SearchScreen: View {

    @ObservedObject var player: PlayerEngine
    @StateObject private var viewModel = SearchViewModel()

    /// `onTrackClick` de `SearchScreen.kt`: al tocar un resultado, Android abre el
    /// reproductor ademas de ponerlo a sonar.
    var onTrackOpened: () -> Void = {}

    var body: some View {
        ZStack {
            EMusicColor.background.ignoresSafeArea()

            VStack(spacing: 0) {
                searchField

                // Mismo placeholder que Android mientras busca (TrackListSkeleton).
                if viewModel.isLoading {
                    TrackListSkeleton()
                        .padding(.top, 8)
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
                                onTrackOpened()
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

// Ya no quedan pantallas pendientes: las tres pestañas estan portadas.

#Preview {
    ContentView().preferredColorScheme(.dark)
}
