import SwiftUI
import shared

/// Puerto de `ui/player/PlayerScreen.kt`. Se abre tocando el mini reproductor y se cierra
/// arrastrando hacia abajo o con la flecha, igual que en Android.
struct PlayerScreen: View {

    @ObservedObject var player: PlayerEngine
    @Environment(\.dismiss) private var dismiss

    @StateObject private var dominant = DominantColorLoader()

    /// Arrastre vertical para minimizar (Android: `detectDragGestures` + `translationY`).
    @State private var dragOffsetY: CGFloat = 0

    /// Arrastre horizontal sobre la caratula para pasar de cancion.
    @State private var swipeOffset: CGFloat = 0

    /// Mientras se arrastra el slider mandan los dedos, no el reproductor.
    @State private var scrubPosition: Double?

    @State private var showQueue = false

    /// En Android es `loadingTrack != null`: la URL todavia se esta resolviendo.
    private var isResolving: Bool { player.isBuffering }

    var body: some View {
        ZStack {
            // Gradiente dinamico: color dominante de la caratula arriba, fondo abajo.
            LinearGradient(
                colors: [dominant.color.opacity(0.55), EMusicColor.background, EMusicColor.background],
                startPoint: .top,
                endPoint: .bottom
            )
            .ignoresSafeArea()

            content
                .offset(y: max(dragOffsetY, 0))
                .opacity(dismissOpacity * contentAlpha)
        }
        .gesture(dismissDrag)
        .onAppear { dominant.load(from: artworkForColor) }
        .onChange(of: player.currentTrack?.videoId) { _ in
            dominant.load(from: artworkForColor)
            swipeOffset = 0
            scrubPosition = nil
        }
        .sheet(isPresented: $showQueue) {
            QueueSheet(player: player)
        }
    }

    /// La miniatura ORIGINAL, no la de maxima calidad: maxresdefault puede dar 404 y
    /// dejaria el gradiente sin color. Mismo criterio que el comentario de Android.
    private var artworkForColor: String? {
        guard let track = player.currentTrack else { return nil }
        let original = track.displayThumbnail
        return original.isEmpty ? track.hqThumbnail : original
    }

    /// Mientras se resuelve el stream se atenua el contenido en vez de taparlo con un
    /// skeleton opaco: la foto y el titulo se siguen leyendo.
    private var contentAlpha: Double {
        isResolving && player.errorMessage == nil ? 0.45 : 1
    }

    private var dismissOpacity: Double {
        1 - min(max(dragOffsetY, 0) / 1200, 0.5)
    }

    private var dismissDrag: some Gesture {
        DragGesture()
            .onChanged { value in
                if value.translation.height > 0 {
                    dragOffsetY = value.translation.height
                }
            }
            .onEnded { _ in
                if dragOffsetY > 300 {
                    dismiss()
                }
                withAnimation(.spring(response: 0.3)) { dragOffsetY = 0 }
            }
    }

    // MARK: - Contenido

    private var content: some View {
        VStack(spacing: 0) {
            grabber
            topBar

            Spacer(minLength: 0).frame(maxHeight: 28)

            artwork

            Spacer(minLength: 0).frame(maxHeight: 24)

            titleRow
                .padding(.top, 24)

            progressSection
                .padding(.top, 20)

            controls
                .padding(.top, 16)

            Spacer(minLength: 0)
        }
        .padding(.horizontal, 24)
    }

    private var grabber: some View {
        Capsule()
            .fill(EMusicColor.onSurfaceVariant.opacity(0.35))
            .frame(width: 40, height: 4)
            .padding(.top, 10)
    }

    private var topBar: some View {
        HStack {
            Button { dismiss() } label: {
                Image(systemName: "chevron.down")
                    .font(.system(size: 20, weight: .medium))
                    .foregroundStyle(EMusicColor.onSurface)
                    .frame(width: 40, height: 40)
            }

            Spacer()

            Text("REPRODUCIENDO")
                .font(.caption2.weight(.semibold))
                .kerning(1.6)
                .foregroundStyle(EMusicColor.onSurfaceVariant)

            Spacer()

            Button { showQueue = true } label: {
                Image(systemName: "list.bullet")
                    .font(.system(size: 18, weight: .medium))
                    .foregroundStyle(EMusicColor.onSurface)
                    .frame(width: 40, height: 40)
            }
        }
        .padding(.top, 6)
        .padding(.bottom, 6)
    }

    // MARK: Caratula

    private var artwork: some View {
        GeometryReader { geo in
            ZStack {
                // Caratula PROGRESIVA, igual que en Android: primero hqdefault (siempre
                // existe, aparece al instante) y encima maxresdefault cuando termina de
                // cargar; si esa da 404 se cae a sddefault.
                artworkImage(url: player.currentTrack?.sdThumbnail)

                AsyncImage(url: URL(string: player.currentTrack?.hqThumbnail ?? "")) { phase in
                    switch phase {
                    case .success(let image):
                        image.resizable().aspectRatio(contentMode: .fill)
                    case .failure:
                        artworkImage(url: player.currentTrack?.sddThumbnail)
                    default:
                        Color.clear
                    }
                }
                .frame(width: geo.size.width, height: geo.size.width)
                .clipped()

                if player.errorMessage != nil {
                    errorOverlay
                } else if isResolving {
                    // Sin scrim: la caratula se sigue viendo, solo el spinner.
                    ProgressView()
                        .progressViewStyle(.circular)
                        .tint(.white)
                        .scaleEffect(1.4)
                }
            }
            .frame(width: geo.size.width, height: geo.size.width)
            .clipShape(RoundedRectangle(cornerRadius: 20))
            .shadow(color: .black.opacity(0.55), radius: 28, y: 12)
            .scaleEffect(player.isPlaying ? 1 : 0.9)
            .animation(.easeInOut(duration: 0.35), value: player.isPlaying)
            .offset(x: swipeOffset)
            .opacity(1 - min(abs(swipeOffset) / 800, 0.3))
            .gesture(artworkSwipe)
        }
        .aspectRatio(1, contentMode: .fit)
    }

    private func artworkImage(url: String?) -> some View {
        AsyncImage(url: URL(string: url ?? "")) { image in
            image.resizable().aspectRatio(contentMode: .fill)
        } placeholder: {
            Rectangle().fill(EMusicColor.surfaceVariant)
        }
    }

    private var artworkSwipe: some Gesture {
        DragGesture()
            .onChanged { value in
                // Solo horizontal: el arrastre vertical lo maneja el cierre de pantalla.
                if abs(value.translation.width) > abs(value.translation.height) {
                    swipeOffset = value.translation.width
                }
            }
            .onEnded { _ in
                if swipeOffset < -200 {
                    player.next()
                } else if swipeOffset > 200 {
                    player.previous()
                }
                withAnimation(.spring(response: 0.3)) { swipeOffset = 0 }
            }
    }

    private var errorOverlay: some View {
        ZStack {
            Color.black.opacity(0.6)
            VStack(spacing: 8) {
                Image(systemName: "exclamationmark.circle")
                    .font(.system(size: 36))
                    .foregroundStyle(.white)
                Text("No se pudo reproducir")
                    .font(.subheadline)
                    .foregroundStyle(.white)
                Button { player.retry() } label: {
                    Label("Reintentar", systemImage: "arrow.clockwise")
                        .font(.subheadline)
                        .padding(.horizontal, 16)
                        .padding(.vertical, 8)
                        .background(EMusicColor.surfaceVariant)
                        .foregroundStyle(EMusicColor.onSurface)
                        .clipShape(Capsule())
                }
                .padding(.top, 4)
            }
        }
    }

    // MARK: Titulo y acciones

    private var titleRow: some View {
        HStack {
            VStack(alignment: .leading, spacing: 2) {
                Text(player.currentTrack?.title ?? "Sin reproducción")
                    .font(.title3.bold())
                    .foregroundStyle(EMusicColor.onSurface)
                    .lineLimit(1)
                Text(player.currentTrack?.displayArtist ?? "")
                    .font(.body)
                    .foregroundStyle(EMusicColor.onSurfaceVariant)
                    .lineLimit(1)
            }

            Spacer(minLength: 8)

            Button { player.toggleFavorite() } label: {
                Image(systemName: player.isFavorite ? "heart.fill" : "heart")
                    .font(.system(size: 24))
                    .foregroundStyle(player.isFavorite ? EMusicColor.favorite : EMusicColor.onSurfaceVariant)
                    .frame(width: 44, height: 44)
            }
            .animation(.easeInOut(duration: 0.2), value: player.isFavorite)
        }
    }

    // MARK: Progreso

    private var progressSection: some View {
        VStack(spacing: 4) {
            if isResolving {
                // Barra atenuada y tiempos "--:--" mientras no hay reproduccion real.
                Capsule()
                    .fill(EMusicColor.onSurface.opacity(0.15))
                    .frame(height: 4)
                    .padding(.vertical, 18)

                HStack {
                    Text("--:--")
                    Spacer()
                    Text("--:--")
                }
                .font(.caption2)
                .foregroundStyle(EMusicColor.onSurfaceVariant)
            } else {
                Slider(
                    value: Binding(
                        get: { scrubPosition ?? player.position },
                        set: { scrubPosition = $0 }
                    ),
                    in: 0...max(player.duration, 1),
                    onEditingChanged: { editing in
                        if !editing, let target = scrubPosition {
                            player.seek(to: target)
                            scrubPosition = nil
                        }
                    }
                )
                .tint(EMusicColor.primary)

                HStack {
                    Text(formatDuration(Int32(scrubPosition ?? player.position)))
                    Spacer()
                    Text(formatDuration(Int32(player.duration)))
                }
                .font(.caption2.monospacedDigit())
                .foregroundStyle(EMusicColor.onSurfaceVariant)
            }
        }
    }

    // MARK: Controles

    private var controls: some View {
        HStack {
            Spacer()

            Button { player.toggleShuffle() } label: {
                Image(systemName: "shuffle")
                    .font(.system(size: 22))
                    .foregroundStyle(player.shuffleEnabled ? EMusicColor.primary : EMusicColor.onSurfaceVariant)
                    .frame(width: 44, height: 44)
            }

            Spacer()

            Button { player.previous() } label: {
                Image(systemName: "backward.end.fill")
                    .font(.system(size: 32))
                    .foregroundStyle(EMusicColor.onSurface)
                    .frame(width: 60, height: 60)
            }

            Spacer()

            Button { player.togglePlayPause() } label: {
                ZStack {
                    Circle()
                        .fill(EMusicColor.primary)
                        .frame(width: 72, height: 72)
                    if isResolving {
                        ProgressView().tint(EMusicColor.onPrimary)
                    } else {
                        Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                            .font(.system(size: 32))
                            .foregroundStyle(EMusicColor.onPrimary)
                    }
                }
            }
            .disabled(isResolving)

            Spacer()

            Button { player.next() } label: {
                Image(systemName: "forward.end.fill")
                    .font(.system(size: 32))
                    .foregroundStyle(EMusicColor.onSurface)
                    .frame(width: 60, height: 60)
            }

            Spacer()

            Button { player.cycleRepeat() } label: {
                Image(systemName: player.repeatMode == .one ? "repeat.1" : "repeat")
                    .font(.system(size: 22))
                    .foregroundStyle(player.repeatMode == .off ? EMusicColor.onSurfaceVariant : EMusicColor.primary)
                    .frame(width: 44, height: 44)
            }

            Spacer()
        }
    }
}

// MARK: - Cola

/// Lista de lo que va a sonar. En modo radio se va estirando sola, por eso `queue` es
/// `@Published` en `PlayerEngine`.
private struct QueueSheet: View {

    @ObservedObject var player: PlayerEngine
    @Environment(\.dismiss) private var dismiss

    var body: some View {
        NavigationStack {
            ZStack {
                EMusicColor.background.ignoresSafeArea()

                ScrollView {
                    LazyVStack(spacing: 0) {
                        ForEach(Array(player.queue.enumerated()), id: \.offset) { position, track in
                            Button {
                                player.seekToIndex(position)
                                dismiss()
                            } label: {
                                HStack(spacing: 12) {
                                    Image(systemName: position == player.index ? "speaker.wave.2.fill" : "music.note")
                                        .font(.footnote)
                                        .foregroundStyle(position == player.index
                                                         ? EMusicColor.primary
                                                         : EMusicColor.onSurfaceVariant)
                                        .frame(width: 22)

                                    VStack(alignment: .leading, spacing: 2) {
                                        Text(track.title)
                                            .font(.subheadline)
                                            .foregroundStyle(position == player.index
                                                             ? EMusicColor.primary
                                                             : EMusicColor.onSurface)
                                            .lineLimit(1)
                                        Text(track.displayArtist)
                                            .font(.caption2)
                                            .foregroundStyle(EMusicColor.onSurfaceVariant)
                                            .lineLimit(1)
                                    }

                                    Spacer(minLength: 4)

                                    Text(formatDuration(track.duration))
                                        .font(.caption2.monospacedDigit())
                                        .foregroundStyle(EMusicColor.onSurfaceVariant)
                                }
                                .padding(.horizontal, 16)
                                .padding(.vertical, 10)
                                .contentShape(Rectangle())
                            }
                            .buttonStyle(.plain)
                        }
                    }
                }
            }
            .navigationTitle("Cola")
            .navigationBarTitleDisplayMode(.inline)
            .toolbar {
                ToolbarItem(placement: .topBarTrailing) {
                    Button("Cerrar") { dismiss() }
                        .foregroundStyle(EMusicColor.primary)
                }
            }
        }
        .preferredColorScheme(.dark)
    }
}
