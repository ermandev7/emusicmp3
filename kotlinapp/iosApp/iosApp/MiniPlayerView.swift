import SwiftUI
import shared

/// Puerto de `ui/components/MiniPlayer.kt`: barra sobre la navegacion con la caratula,
/// el titulo, los controles y una linea de progreso verde de 2px abajo.
struct MiniPlayerView: View {

    @ObservedObject var player: PlayerEngine

    var body: some View {
        if let track = player.currentTrack {
            VStack(spacing: 0) {
                HStack(spacing: 12) {
                    AsyncImage(url: URL(string: track.sdThumbnail)) { image in
                        image.resizable().aspectRatio(contentMode: .fill)
                    } placeholder: {
                        Rectangle().fill(EMusicColor.surface)
                    }
                    .frame(
                        width: EMusicMetrics.miniPlayerThumbnailSize,
                        height: EMusicMetrics.miniPlayerThumbnailSize
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

                    Button { player.previous() } label: {
                        Image(systemName: "backward.end.fill")
                            .font(.title3)
                            .foregroundStyle(EMusicColor.onSurface)
                    }

                    // Boton grande verde, la marca visual del reproductor.
                    Button { player.togglePlayPause() } label: {
                        ZStack {
                            Circle()
                                .fill(EMusicColor.primary)
                                .frame(width: 52, height: 52)
                            if player.isBuffering {
                                ProgressView()
                                    .tint(EMusicColor.onPrimary)
                            } else {
                                Image(systemName: player.isPlaying ? "pause.fill" : "play.fill")
                                    .font(.title2)
                                    .foregroundStyle(EMusicColor.onPrimary)
                            }
                        }
                    }

                    Button { player.next() } label: {
                        Image(systemName: "forward.end.fill")
                            .font(.title3)
                            .foregroundStyle(EMusicColor.onSurface)
                    }
                }
                .padding(.horizontal, 12)
                .padding(.vertical, 8)

                // Barra de progreso: 2px, verde sobre gris muy tenue.
                GeometryReader { geo in
                    ZStack(alignment: .leading) {
                        Rectangle()
                            .fill(EMusicColor.onSurface.opacity(0.15))
                        Rectangle()
                            .fill(EMusicColor.primary)
                            .frame(width: geo.size.width * player.progress)
                    }
                }
                .frame(height: EMusicMetrics.miniPlayerProgressHeight)
            }
            .background(EMusicColor.surfaceVariant)
            .clipShape(
                UnevenRoundedRectangle(
                    topLeadingRadius: EMusicMetrics.miniPlayerTopRadius,
                    topTrailingRadius: EMusicMetrics.miniPlayerTopRadius
                )
            )
            .transition(.move(edge: .bottom))
        }
    }
}
