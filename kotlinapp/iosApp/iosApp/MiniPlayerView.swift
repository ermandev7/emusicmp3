import SwiftUI
import shared

/// Puerto de `ui/components/MiniPlayer.kt`, con una desviacion de forma pedida para iOS:
/// en vez de una barra pegada al borde inferior, es una burbuja flotante identica a la de
/// navegacion — mismos margenes, mismo alto y mismo radio (ver `EMusicMetrics`).
///
/// El contenido es el mismo que en Android: caratula, titulo, artista, anterior, el boton
/// verde grande de play/pausa, siguiente y la linea de progreso abajo.
struct MiniPlayerView: View {

    @ObservedObject var player: PlayerEngine

    /// Tocar la caratula o el titulo abre el reproductor completo, como en Android.
    var onExpand: () -> Void = {}

    var body: some View {
        if let track = player.currentTrack {
            // En columna, no superpuesto: la linea de progreso tiene su propio sitio con
            // aire por encima, en vez de ir apoyada en el borde por debajo de la caratula.
            VStack(spacing: 7) {
                HStack(spacing: 10) {
                    // Caratula + textos: toda esta zona abre el reproductor completo.
                    // Los botones quedan fuera del gesto para que sigan funcionando.
                    HStack(spacing: 10) {
                        AsyncImage(url: URL(string: track.sdThumbnail)) { image in
                            image.resizable().aspectRatio(contentMode: .fill)
                        } placeholder: {
                            Rectangle().fill(EMusicColor.surface)
                        }
                        .frame(
                            width: EMusicMetrics.bubbleContentHeight,
                            height: EMusicMetrics.bubbleContentHeight
                        )
                        .clipShape(RoundedRectangle(cornerRadius: EMusicMetrics.miniPlayerThumbnailRadius))

                        VStack(alignment: .leading, spacing: 2) {
                            Text(track.title)
                                .font(.subheadline)
                                .foregroundStyle(EMusicColor.onSurface)
                                .lineLimit(1)
                            Text(track.displayArtist)
                                .font(.caption2)
                                .foregroundStyle(EMusicColor.onSurfaceVariant)
                                .lineLimit(1)
                        }

                        Spacer(minLength: 4)
                    }
                    .contentShape(Rectangle())
                    .onTapGesture { onExpand() }

                    Button { player.previous() } label: {
                        Image(systemName: "backward.end.fill")
                            .font(.system(size: 17))
                            .foregroundStyle(EMusicColor.onSurface)
                    }

                    // Boton grande verde, la marca visual del reproductor.
                    Button { player.togglePlayPause() } label: {
                        ZStack {
                            Circle()
                                .fill(EMusicColor.primary)
                                .frame(
                                    width: EMusicMetrics.bubbleContentHeight,
                                    height: EMusicMetrics.bubbleContentHeight
                                )
                            if player.isBuffering {
                                ProgressView()
                                    .tint(EMusicColor.onPrimary)
                            } else {
                                Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                                    .font(.system(size: 20))
                                    .foregroundStyle(EMusicColor.onPrimary)
                            }
                        }
                    }

                    Button { player.next() } label: {
                        Image(systemName: "forward.end.fill")
                            .font(.system(size: 17))
                            .foregroundStyle(EMusicColor.onSurface)
                    }
                }
                .frame(height: EMusicMetrics.bubbleContentHeight)
                .padding(.leading, 10)
                .padding(.trailing, 14)

                // Linea de progreso con margenes propios y extremos redondeados, para que
                // se lea como una barra dentro de la burbuja y no como el borde de esta.
                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        Capsule()
                            .fill(EMusicColor.onSurface.opacity(0.18))
                        Capsule()
                            .fill(EMusicColor.primary)
                            .frame(width: geo.size.width * player.progress)
                    }
                }
                .frame(height: 3)
                .padding(.horizontal, 14)
            }
            .padding(.vertical, 8)
            .frame(height: EMusicMetrics.bubbleHeight)
            .background(EMusicColor.surfaceVariant)
            .clipShape(RoundedRectangle(cornerRadius: EMusicMetrics.bubbleRadius))
            .shadow(color: .black.opacity(0.35), radius: 10, y: 4)
            .transition(.move(edge: .bottom).combined(with: .opacity))
        }
    }
}

/// Barra de navegacion como segunda burbuja, gemela de la del reproductor. Sustituye a la
/// barra nativa de iOS (que se oculta en `ContentView`) para poder darle la misma forma.
struct BottomNavBubble: View {

    @Binding var selectedTab: Int

    /// Mismas tres secciones y en el mismo orden que `AppNavigation.kt`.
    private let items: [(title: String, icon: String)] = [
        ("Inicio", "house.fill"),
        ("Buscar", "magnifyingglass"),
        ("Biblioteca", "music.note.list"),
    ]

    var body: some View {
        HStack(spacing: 0) {
            ForEach(items.indices, id: \.self) { index in
                let selected = selectedTab == index
                Button {
                    selectedTab = index
                } label: {
                    VStack(spacing: 3) {
                        Image(systemName: items[index].icon)
                            .font(.system(size: 19))
                        Text(items[index].title)
                            .font(.system(size: 11, weight: selected ? .semibold : .regular))
                    }
                    .foregroundStyle(selected ? EMusicColor.primary : EMusicColor.onSurfaceVariant)
                    .frame(maxWidth: .infinity)
                    .contentShape(Rectangle())
                }
                .buttonStyle(.plain)
            }
        }
        .frame(height: EMusicMetrics.bubbleHeight)
        .background(EMusicColor.surfaceVariant)
        .clipShape(RoundedRectangle(cornerRadius: EMusicMetrics.bubbleRadius))
        .shadow(color: .black.opacity(0.35), radius: 10, y: 4)
    }
}
