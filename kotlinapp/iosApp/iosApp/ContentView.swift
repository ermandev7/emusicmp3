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

    @ObservedObject private var connectivity = Connectivity.shared

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
                // Aviso de que no hay red. Va aqui, encima de las burbujas, para que se vea
                // en las tres pestañas: sin esto, sin conexion las listas salian vacias sin
                // decir por que, que es indistinguible de "no tienes nada guardado".
                if !connectivity.isOnline {
                    HStack(spacing: 8) {
                        Image(systemName: "wifi.slash")
                            .font(.footnote)
                        Text("Sin conexión · solo tus descargas")
                            .font(.footnote)
                    }
                    .foregroundStyle(EMusicColor.onSurface)
                    .padding(.horizontal, 14)
                    .padding(.vertical, 8)
                    .background(EMusicColor.surface)
                    .clipShape(Capsule())
                    .transition(.move(edge: .bottom).combined(with: .opacity))
                }

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
        .animation(.easeInOut(duration: 0.25), value: connectivity.isOnline)
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

                // Mientras no se haya buscado nada se muestran las recientes, no un
                // "sin resultados" que todavia no significa nada. Igual que Android.
                if !viewModel.hasSearched && !viewModel.isLoading {
                    if viewModel.recentSearches.isEmpty {
                        emptyPrompt
                    } else {
                        recentList
                    }
                } else {
                    resultsList
                }
            }
        }
    }

    private var resultsList: some View {
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
                            isPlaying: player.currentTrack?.videoId == track.videoId && player.isPlaying
                        )
                    }
                    .buttonStyle(.plain)
                }
            }
            // Espacio para que el mini reproductor no tape la ultima fila.
            Color.clear.frame(height: EMusicMetrics.bottomContentInset)
        }
    }

    /// Puerto de `RecentSearches` en `SearchScreen.kt`: reloj, texto y la X para quitarla.
    private var recentList: some View {
        ScrollView {
            LazyVStack(alignment: .leading, spacing: 0) {
                Text("Búsquedas recientes")
                    .font(.subheadline)
                    .foregroundStyle(EMusicColor.onSurfaceVariant)
                    .padding(.leading, 16)
                    .padding(.top, 12)
                    .padding(.bottom, 4)

                ForEach(viewModel.recentSearches, id: \.self) { text in
                    HStack(spacing: 16) {
                        Button {
                            Task { await viewModel.search(text) }
                        } label: {
                            HStack(spacing: 16) {
                                Image(systemName: "clock.arrow.circlepath")
                                    .font(.system(size: 17))
                                    .foregroundStyle(EMusicColor.onSurfaceVariant)
                                Text(text)
                                    .font(.body)
                                    .foregroundStyle(EMusicColor.onSurface)
                                    .lineLimit(1)
                                Spacer(minLength: 4)
                            }
                            .contentShape(Rectangle())
                        }
                        .buttonStyle(.plain)

                        Button {
                            viewModel.removeRecent(text)
                        } label: {
                            Image(systemName: "xmark")
                                .font(.system(size: 15))
                                .foregroundStyle(EMusicColor.onSurfaceVariant)
                                .frame(width: 32, height: 32)
                        }
                    }
                    .padding(.horizontal, 16)
                    .padding(.vertical, 12)
                }
            }
            Color.clear.frame(height: EMusicMetrics.bottomContentInset)
        }
    }

    private var emptyPrompt: some View {
        VStack(spacing: 8) {
            Image(systemName: "magnifyingglass")
                .font(.system(size: 48))
                .foregroundStyle(EMusicColor.onSurfaceVariant.opacity(0.4))
            Text("Escribe para buscar")
                .font(.subheadline)
                .foregroundStyle(EMusicColor.onSurfaceVariant)
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
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
                    viewModel.clear()
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

    /// Igual que en `TrackItem.kt`, esta bandera hace dos cosas a la vez: pinta el titulo
    /// en verde y cambia la duracion por las barritas de ecualizador.
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

            if isPlaying {
                NowPlayingBars()
            } else {
                Text(formatDuration(track.duration))
                    .font(.caption.monospacedDigit())
                    .foregroundStyle(EMusicColor.onSurfaceVariant)
            }
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
