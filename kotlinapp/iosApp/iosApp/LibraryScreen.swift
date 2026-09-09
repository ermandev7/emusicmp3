import SwiftUI
import shared

/// Puerto de `ui/library/LibraryScreen.kt`: "Mi Biblioteca" con los cuatro chips
/// deslizables (Favoritos / Historial / Descargas / Playlists) y la lista de la pestaña
/// activa debajo.
struct LibraryScreen: View {

    @ObservedObject var player: PlayerEngine
    @StateObject private var viewModel = LibraryViewModel()
    @ObservedObject private var store = DownloadStore.shared
    @ObservedObject private var downloads = DownloadManager.shared
    @ObservedObject private var connectivity = Connectivity.shared

    @State private var pendingDelete: DownloadedTrack?

    /// Abre el reproductor al poner algo a sonar, igual que el `onTrackClick` de Android.
    var onTrackOpened: () -> Void = {}

    @State private var showCreatePlaylist = false
    @State private var newPlaylistName = ""
    /// `navigationDestination(item:)` es de iOS 17 y el proyecto apunta a iOS 16, asi que
    /// se usa la variante con `isPresented` y la playlist se guarda aparte.
    @State private var openPlaylist: Playlist?
    @State private var showPlaylistDetail = false

    var body: some View {
        NavigationStack {
            ZStack(alignment: .bottomTrailing) {
                EMusicColor.background.ignoresSafeArea()

                VStack(alignment: .leading, spacing: 0) {
                    Text("Mi Biblioteca")
                        .font(.title2.bold())
                        .foregroundStyle(EMusicColor.onSurface)
                        .padding(.horizontal, 20)
                        .padding(.top, 4)
                        .padding(.bottom, 10)

                    tabChips

                    if let error = viewModel.errorMessage {
                        Text(error)
                            .font(.footnote)
                            .foregroundStyle(EMusicColor.favorite)
                            .padding(.horizontal, 16)
                            .padding(.top, 8)
                    }

                    // El esqueleto de carga NUNCA tapa Descargas: esa pestaña lee del disco
                    // y tiene que poder abrirse aunque no haya red ni respuesta del servidor.
                    if viewModel.isLoading && viewModel.tab != .downloads {
                        TrackListSkeleton()
                            .padding(.top, 10)
                        Spacer()
                    } else {
                        tabContent
                    }
                }

                // FAB de "Nueva playlist": en Android solo aparece en esa pestaña.
                if viewModel.tab == .playlists {
                    Button {
                        showCreatePlaylist = true
                    } label: {
                        Image(systemName: "plus")
                            .font(.system(size: 22, weight: .medium))
                            .foregroundStyle(EMusicColor.onPrimary)
                            .frame(width: 56, height: 56)
                            .background(EMusicColor.primary)
                            .clipShape(RoundedRectangle(cornerRadius: 16))
                            .shadow(color: .black.opacity(0.4), radius: 8, y: 4)
                    }
                    .padding(.trailing, 20)
                    .padding(.bottom, EMusicMetrics.bottomContentInset)
                }
            }
            .navigationBarHidden(true)
            .navigationDestination(isPresented: $showPlaylistDetail) {
                if let playlist = openPlaylist {
                    PlaylistDetailScreen(
                        playlist: playlist,
                        player: player,
                        onTrackOpened: onTrackOpened,
                        onRemove: { videoId in
                            viewModel.removeTrack(videoId, from: playlist.id)
                        }
                    )
                }
            }
        }
        .task { await viewModel.loadAll() }
        // Al recuperar la conexion se recarga solo: si no, la pantalla se quedaria con las
        // listas vacias que dejo el modo sin red hasta que el usuario reiniciara la app.
        .onChange(of: connectivity.isOnline) { online in
            if online { Task { await viewModel.loadAll() } }
        }
        // Al volver de otra pestaña o del reproductor se refresca la lista visible, que es
        // lo que hace el observador de ON_RESUME en Android.
        .onChange(of: player.isFavorite) { _ in
            Task { await viewModel.refreshCurrentTab() }
        }
        .alert("Nueva playlist", isPresented: $showCreatePlaylist) {
            TextField("Nombre", text: $newPlaylistName)
            Button("Cancelar", role: .cancel) { newPlaylistName = "" }
            Button("Crear") {
                viewModel.createPlaylist(named: newPlaylistName)
                newPlaylistName = ""
            }
        }
        // Borrar una descarga es irreversible y libera espacio: se confirma, como en Android.
        .alert(
            "Eliminar descarga",
            isPresented: Binding(
                get: { pendingDelete != nil },
                set: { if !$0 { pendingDelete = nil } }
            ),
            presenting: pendingDelete
        ) { item in
            Button("Cancelar", role: .cancel) { pendingDelete = nil }
            Button("Eliminar", role: .destructive) {
                store.remove(item.videoId)
                pendingDelete = nil
            }
        } message: { item in
            Text("¿Eliminar «\(item.title)» del dispositivo? Esta acción no se puede deshacer.")
        }
    }

    // MARK: Chips

    private var tabChips: some View {
        ScrollView(.horizontal, showsIndicators: false) {
            HStack(spacing: 8) {
                ForEach(LibraryTab.allCases) { tab in
                    let selected = viewModel.tab == tab
                    Button {
                        viewModel.tab = tab
                    } label: {
                        HStack(spacing: 6) {
                            Image(systemName: tab.icon)
                                .font(.system(size: 15))
                            Text(tab.title)
                                .font(.subheadline)
                        }
                        .foregroundStyle(selected ? EMusicColor.onSecondaryContainer : EMusicColor.onSurface)
                        .padding(.horizontal, 14)
                        .padding(.vertical, 8)
                        .background(selected ? EMusicColor.secondaryContainer : Color.clear)
                        .overlay(
                            Capsule().stroke(
                                selected ? Color.clear : EMusicColor.outline,
                                lineWidth: 1
                            )
                        )
                        .clipShape(Capsule())
                    }
                    .buttonStyle(.plain)
                }
            }
            .padding(.horizontal, 16)
        }
    }

    // MARK: Contenido

    @ViewBuilder
    private var tabContent: some View {
        switch viewModel.tab {
        case .favorites:
            trackList(viewModel.favorites, emptyMessage: "No tienes favoritos aún")

        case .history:
            trackList(viewModel.history, emptyMessage: "El historial está vacío")

        case .downloads:
            downloadsList

        case .playlists:
            playlistList
        }
    }

    @ViewBuilder
    private func trackList(_ tracks: [Track], emptyMessage: String) -> some View {
        if tracks.isEmpty {
            emptyState(icon: "music.note", title: emptyMessage, detail: nil)
        } else {
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(Array(tracks.enumerated()), id: \.offset) { _, track in
                        Button {
                            player.play(track: track, contextQueue: tracks)
                            onTrackOpened()
                        } label: {
                            LibraryTrackRow(
                                track: track,
                                isPlaying: player.currentTrack?.videoId == track.videoId && player.isPlaying
                            )
                        }
                        .buttonStyle(.plain)
                    }
                }
                Color.clear.frame(height: EMusicMetrics.bottomContentInset)
            }
            .padding(.top, 10)
        }
    }

    /// Puerto de `DownloadsList` en `LibraryScreen.kt`: la descarga en curso arriba con su
    /// porcentaje, y debajo lo que ya está en el dispositivo.
    @ViewBuilder
    private var downloadsList: some View {
        let showActive = downloads.state.isDownloading

        if store.downloads.isEmpty && !showActive {
            emptyState(
                icon: "arrow.down.circle",
                title: "No tienes descargas aún",
                detail: "Descarga canciones desde el reproductor."
            )
        } else {
            ScrollView {
                LazyVStack(spacing: 0) {
                    if showActive {
                        activeDownloadRow
                    }

                    ForEach(store.downloads) { item in
                        HStack(spacing: 0) {
                            Button {
                                // Se encola la lista entera de descargas: así el siguiente
                                // también suena sin conexión.
                                let queue = store.downloads.map { $0.toTrack() }
                                player.play(track: item.toTrack(), contextQueue: queue)
                                onTrackOpened()
                            } label: {
                                LibraryTrackRow(
                                    track: item.toTrack(),
                                    isPlaying: player.currentTrack?.videoId == item.videoId && player.isPlaying
                                )
                            }
                            .buttonStyle(.plain)

                            Button {
                                pendingDelete = item
                            } label: {
                                Image(systemName: "trash")
                                    .foregroundStyle(EMusicColor.favorite)
                                    .frame(width: 44, height: 44)
                            }
                            .padding(.trailing, 4)
                        }
                    }
                }
                Color.clear.frame(height: EMusicMetrics.bottomContentInset)
            }
            .padding(.top, 10)
        }
    }

    private var activeDownloadRow: some View {
        HStack(spacing: EMusicMetrics.trackRowSpacing) {
            AsyncImage(url: URL(string: downloads.state.thumbnailUrl)) { image in
                image.resizable().aspectRatio(contentMode: .fill)
            } placeholder: {
                Rectangle().fill(EMusicColor.surfaceVariant)
            }
            .frame(width: EMusicMetrics.trackThumbnailSize, height: EMusicMetrics.trackThumbnailSize)
            .clipShape(RoundedRectangle(cornerRadius: EMusicMetrics.trackThumbnailRadius))

            VStack(alignment: .leading, spacing: 4) {
                Text(downloads.state.title.isEmpty ? "Descargando…" : downloads.state.title)
                    .font(.body)
                    .foregroundStyle(EMusicColor.primary)
                    .lineLimit(1)

                let pctText = downloads.state.progress > 0
                    ? "Descargando \(downloads.state.progress)%"
                    : "Descargando…"
                Text(downloads.state.artist.isEmpty ? pctText : "\(downloads.state.artist) · \(pctText)")
                    .font(.caption2)
                    .foregroundStyle(EMusicColor.onSurfaceVariant)
                    .lineLimit(1)

                // Barra determinada solo si el servidor dijo cuánto pesa; si no,
                // indeterminada, para no inventarse un porcentaje.
                if downloads.state.progress > 0 {
                    ProgressView(value: Double(downloads.state.progress), total: 100)
                        .tint(EMusicColor.primary)
                } else {
                    ProgressView().progressViewStyle(.linear).tint(EMusicColor.primary)
                }
            }

            Button { downloads.cancel() } label: {
                Image(systemName: "xmark")
                    .font(.footnote)
                    .foregroundStyle(EMusicColor.onSurfaceVariant)
                    .frame(width: 32, height: 32)
            }
        }
        .padding(.horizontal, EMusicMetrics.trackRowHorizontalPadding)
        .padding(.vertical, EMusicMetrics.trackRowVerticalPadding)
    }

    @ViewBuilder
    private var playlistList: some View {
        if viewModel.playlists.isEmpty {
            emptyState(icon: "music.note.list", title: "No tienes playlists aún", detail: nil)
        } else {
            ScrollView {
                LazyVStack(spacing: 0) {
                    ForEach(viewModel.playlists, id: \.id) { playlist in
                        HStack(spacing: 12) {
                            Button {
                                openPlaylist = playlist
                                showPlaylistDetail = true
                            } label: {
                                HStack(spacing: 12) {
                                    if let cover = playlist.coverUrl, !cover.isEmpty {
                                        AsyncImage(url: URL(string: cover)) { image in
                                            image.resizable().aspectRatio(contentMode: .fill)
                                        } placeholder: {
                                            Rectangle().fill(EMusicColor.surfaceVariant)
                                        }
                                        .frame(width: 48, height: 48)
                                        .clipShape(RoundedRectangle(cornerRadius: 4))
                                    } else {
                                        Image(systemName: "music.note")
                                            .font(.system(size: 24))
                                            .foregroundStyle(EMusicColor.onSurfaceVariant)
                                            .frame(width: 48, height: 48)
                                    }

                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(playlist.name)
                                            .font(.body)
                                            .foregroundStyle(EMusicColor.onSurface)
                                            .lineLimit(1)
                                        Text("\(playlist.trackCount) canciones")
                                            .font(.caption)
                                            .foregroundStyle(EMusicColor.onSurfaceVariant)
                                    }

                                    Spacer(minLength: 4)
                                }
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)

                            Button {
                                viewModel.deletePlaylist(id: playlist.id)
                            } label: {
                                Image(systemName: "trash")
                                    .foregroundStyle(EMusicColor.favorite)
                                    .frame(width: 44, height: 44)
                            }
                        }
                        .padding(.horizontal, 16)
                        .padding(.vertical, 8)
                    }
                }
                Color.clear.frame(height: EMusicMetrics.bottomContentInset)
            }
            .padding(.top, 10)
        }
    }

    private func emptyState(icon: String, title: String, detail: String?) -> some View {
        VStack(spacing: 8) {
            Image(systemName: icon)
                .font(.system(size: 48))
                .foregroundStyle(EMusicColor.onSurfaceVariant)
            Text(title)
                .font(.subheadline)
                .foregroundStyle(EMusicColor.onSurfaceVariant)
            if let detail {
                Text(detail)
                    .font(.caption)
                    .foregroundStyle(EMusicColor.onSurfaceVariant.opacity(0.8))
                    .multilineTextAlignment(.center)
                    .padding(.horizontal, 40)
            }
        }
        .frame(maxWidth: .infinity, maxHeight: .infinity)
    }
}

// MARK: - Detalle de playlist

/// Puerto de `ui/playlist/PlaylistDetailScreen.kt`. Las canciones ya vienen dentro del
/// `Playlist` que devuelve `getPlaylists`, asi que no hace falta otra llamada.
private struct PlaylistDetailScreen: View {

    let playlist: Playlist
    @ObservedObject var player: PlayerEngine
    var onTrackOpened: () -> Void
    var onRemove: (String) -> Void

    /// Copia local para que al quitar una cancion desaparezca al instante.
    @State private var tracks: [Track] = []

    var body: some View {
        ZStack {
            EMusicColor.background.ignoresSafeArea()

            if tracks.isEmpty {
                Text("Esta playlist está vacía")
                    .font(.subheadline)
                    .foregroundStyle(EMusicColor.onSurfaceVariant)
            } else {
                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(Array(tracks.enumerated()), id: \.offset) { _, track in
                            HStack(spacing: 0) {
                                Button {
                                    player.play(track: track, contextQueue: tracks)
                                    onTrackOpened()
                                } label: {
                                    LibraryTrackRow(
                                        track: track,
                                        isPlaying: player.currentTrack?.videoId == track.videoId && player.isPlaying
                                    )
                                }
                                .buttonStyle(.plain)

                                Button {
                                    tracks.removeAll { $0.videoId == track.videoId }
                                    onRemove(track.videoId)
                                } label: {
                                    Image(systemName: "minus.circle")
                                        .foregroundStyle(EMusicColor.onSurfaceVariant)
                                        .frame(width: 40, height: 44)
                                }
                                .padding(.trailing, 8)
                            }
                        }
                    }
                    Color.clear.frame(height: EMusicMetrics.bottomContentInset)
                }
            }
        }
        .navigationTitle(playlist.name)
        .navigationBarTitleDisplayMode(.inline)
        .onAppear { tracks = playlist.tracks }
    }
}

// MARK: - Fila

/// Igual que `TrackItem.kt`: caratula, titulo (verde si suena), artista y duracion.
struct LibraryTrackRow: View {

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

            // Igual que `TrackItem.kt`: cuando suena, la duracion deja paso al ecualizador.
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
