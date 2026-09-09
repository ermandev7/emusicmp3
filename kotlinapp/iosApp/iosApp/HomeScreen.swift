import SwiftUI
import shared

/// Puerto de `ui/home/HomeScreen.kt`. Mismas secciones y en el mismo orden:
/// cabecera, grid de generos, mas escuchadas, generos favoritos y recomendadas.
struct HomeScreen: View {

    @ObservedObject var player: PlayerEngine
    @StateObject private var viewModel = HomeViewModel()

    /// Lo pone el TabView para saltar a Buscar desde la lupa de la cabecera.
    var onSearchTapped: () -> Void = {}

    /// `onTrackClick` de `HomeScreen.kt`: al poner algo a sonar, Android navega a la
    /// pantalla de reproductor. Aca abre el mismo reproductor a pantalla completa.
    var onTrackOpened: () -> Void = {}

    var body: some View {
        ZStack {
            EMusicColor.background.ignoresSafeArea()

            ScrollView {
                LazyVStack(alignment: .leading, spacing: 0) {
                    header

                    if let error = viewModel.errorMessage {
                        Text(error)
                            .font(.footnote)
                            .foregroundStyle(EMusicColor.favorite)
                            .padding(.horizontal, EMusicMetrics.sectionHorizontalPadding)
                            .padding(.bottom, 8)
                    }

                    genreGrid

                    // Mientras llega el historial, filas fantasma con barrido de brillo
                    // en vez de un hueco vacio (TrackListSkeleton(count = 5) en Android).
                    if viewModel.isLoading {
                        SectionTitle("Cargando…")
                        TrackListSkeleton(count: 5)
                    }

                    if !viewModel.mostPlayed.isEmpty {
                        SectionTitle("Más escuchadas")
                        mostPlayedRow
                    }

                    if !viewModel.topGenres.isEmpty {
                        SectionTitle("Tus géneros favoritos")
                        genreChips
                    }

                    if viewModel.isLoadingReco && viewModel.recommendations.isEmpty {
                        SectionTitle("Recomendado para ti")
                        ProgressView()
                            .tint(EMusicColor.primary)
                            .frame(maxWidth: .infinity)
                            .padding(.vertical, 24)
                    }

                    if !viewModel.recommendations.isEmpty {
                        SectionTitle("Recomendado para ti")
                        ForEach(viewModel.recommendations, id: \.videoId) { track in
                            recommendationRow(track)
                        }
                    }

                    if viewModel.isEmpty {
                        emptyState
                    }

                    // Hueco para que el mini reproductor no tape el final.
                    Color.clear.frame(height: EMusicMetrics.bottomContentInset)
                }
            }
        }
        .task { await viewModel.loadAll() }
        .refreshable { await viewModel.loadAll() }
    }

    // MARK: Cabecera

    private var header: some View {
        HStack {
            Text("eMusic")
                .font(.largeTitle.bold())
                .foregroundStyle(EMusicColor.onSurface)

            Spacer()

            Button {
                Task { await viewModel.loadAll() }
            } label: {
                Image(systemName: "arrow.clockwise")
                    .font(.system(size: 20))
                    .foregroundStyle(EMusicColor.onSurface)
            }
            .padding(.trailing, 4)

            Button(action: onSearchTapped) {
                Image(systemName: "magnifyingglass")
                    .font(.system(size: 20))
                    .foregroundStyle(EMusicColor.onSurface)
            }
        }
        .padding(.horizontal, EMusicMetrics.sectionHorizontalPadding)
        .padding(.vertical, 12)
    }

    // MARK: Generos

    /// Grid de 2 columnas con los gradientes de `HomeScreen.kt`.
    private var genreGrid: some View {
        VStack(spacing: EMusicMetrics.genreCardSpacing) {
            SectionTitle("Escuchar por género")
                .frame(maxWidth: .infinity, alignment: .leading)

            LazyVGrid(
                columns: [
                    GridItem(.flexible(), spacing: EMusicMetrics.genreCardSpacing),
                    GridItem(.flexible(), spacing: EMusicMetrics.genreCardSpacing),
                ],
                spacing: EMusicMetrics.genreCardSpacing
            ) {
                ForEach(EMusicGenre.all) { genre in
                    Button {
                        Task {
                            if await viewModel.playGenre(genre, player: player) {
                                onTrackOpened()
                            }
                        }
                    } label: {
                        genreCard(genre)
                    }
                    .buttonStyle(.plain)
                    .disabled(viewModel.loadingGenre != nil)
                }
            }
            .padding(.horizontal, 12)
        }
    }

    private func genreCard(_ genre: EMusicGenre) -> some View {
        HStack(spacing: 8) {
            if viewModel.loadingGenre == genre.name {
                ProgressView()
                    .tint(.white)
                    .scaleEffect(0.7)
                Text("Cargando...")
                    .font(.system(size: 12))
                    .foregroundStyle(.white)
            } else {
                Text(genre.emoji)
                    .font(.system(size: 18))
                Text(genre.name)
                    .font(.system(size: 13, weight: .semibold))
                    .foregroundStyle(.white)
                    .lineLimit(1)
            }
            Spacer(minLength: 0)
        }
        .padding(.horizontal, 12)
        .frame(height: 52)
        .frame(maxWidth: .infinity)
        .background(genre.gradient)
        .clipShape(RoundedRectangle(cornerRadius: 10))
    }

    // MARK: Mas escuchadas

    private var mostPlayedRow: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 10) {
                ForEach(viewModel.mostPlayed.prefix(10), id: \.videoId) { track in
                    Button {
                        player.play(track: track, contextQueue: viewModel.mostPlayed)
                        onTrackOpened()
                    } label: {
                        mostPlayedCard(track)
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.horizontal, EMusicMetrics.sectionHorizontalPadding)
            .padding(.vertical, 4)
        }
    }

    private func mostPlayedCard(_ track: Track) -> some View {
        VStack(alignment: .leading, spacing: 0) {
            AsyncImage(url: URL(string: track.hqThumbnail)) { image in
                image.resizable().aspectRatio(contentMode: .fill)
            } placeholder: {
                Rectangle().fill(EMusicColor.surface)
            }
            .frame(width: 140, height: 110)
            .clipped()

            Text(track.title)
                .font(.caption)
                .foregroundStyle(EMusicColor.onSurface)
                .lineLimit(2)
                .multilineTextAlignment(.leading)
                .frame(width: 140, alignment: .leading)
                .padding(.horizontal, 8)
                .padding(.vertical, 6)
        }
        .background(EMusicColor.surfaceVariant)
        .clipShape(RoundedRectangle(cornerRadius: 8))
    }

    // MARK: Chips de generos

    private var genreChips: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(viewModel.topGenres, id: \.genre) { stat in
                    HStack(spacing: 4) {
                        Text(stat.genre.prefix(1).uppercased() + stat.genre.dropFirst())
                            .font(.footnote)
                            .foregroundStyle(.white)
                        Text("\(stat.count)")
                            .font(.caption2)
                            .foregroundStyle(.white.opacity(0.7))
                    }
                    .padding(.horizontal, 14)
                    .padding(.vertical, 8)
                    .background(EMusicGenre.chipColor(for: stat.genre))
                    .clipShape(Capsule())
                }
            }
            .padding(.horizontal, EMusicMetrics.sectionHorizontalPadding)
            .padding(.vertical, 4)
        }
    }

    // MARK: Recomendadas

    private func recommendationRow(_ track: Track) -> some View {
        HStack(spacing: 0) {
            Button {
                player.play(track: track, contextQueue: viewModel.recommendations)
                onTrackOpened()
            } label: {
                HomeTrackRow(
                    track: track,
                    isPlaying: player.currentTrack?.videoId == track.videoId && player.isPlaying
                )
            }
            .buttonStyle(.plain)

            Menu {
                Button(role: .destructive) {
                    viewModel.exclude(track)
                } label: {
                    Label("No me interesa", systemImage: "hand.thumbsdown")
                }
            } label: {
                Image(systemName: "ellipsis")
                    .foregroundStyle(EMusicColor.onSurfaceVariant)
                    .frame(width: 40, height: 44)
            }
            .padding(.trailing, 8)
        }
    }

    // MARK: Estado vacio

    private var emptyState: some View {
        VStack(spacing: 12) {
            Image(systemName: "music.note")
                .font(.system(size: 48))
                .foregroundStyle(EMusicColor.onSurfaceVariant.opacity(0.5))
            Text("Escucha música para ver tus favoritas aquí")
                .font(.subheadline)
                .foregroundStyle(EMusicColor.onSurfaceVariant)
                .multilineTextAlignment(.center)
            Button("Reintentar") {
                Task { await viewModel.loadAll() }
            }
            .foregroundStyle(EMusicColor.primary)
        }
        .frame(maxWidth: .infinity)
        .padding(48)
    }
}

// MARK: - Piezas reutilizables

/// Titulo de seccion: `titleMedium` en negrita, padding 16/12 como en Compose.
struct SectionTitle: View {
    let text: String
    init(_ text: String) { self.text = text }

    var body: some View {
        Text(text)
            .font(.title3.bold())
            .foregroundStyle(EMusicColor.onSurface)
            .padding(.horizontal, EMusicMetrics.sectionHorizontalPadding)
            .padding(.vertical, 12)
    }
}

/// Igual que la fila del buscador, pero sin la duracion: en Inicio ese espacio lo
/// ocupa el menu de tres puntos (mismo criterio que `TrackItem.kt`).
struct HomeTrackRow: View {
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

            Spacer(minLength: 0)
        }
        .padding(.leading, EMusicMetrics.trackRowHorizontalPadding)
        .padding(.vertical, EMusicMetrics.trackRowVerticalPadding)
        .contentShape(Rectangle())
    }
}
