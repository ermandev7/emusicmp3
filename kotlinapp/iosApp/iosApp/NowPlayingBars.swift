import SwiftUI

/// Puerto de `ui/components/NowPlayingBars.kt`: tres barras de ecualizador animadas que
/// indican "sonando ahora".
///
/// En Compose son tres `animateFloat` con `RepeatMode.Reverse` y duraciones distintas
/// (420, 520 y 360 ms). Los tiempos desiguales son lo que hace que no parezcan latir a la
/// vez; se respetan tal cual.
struct NowPlayingBars: View {

    var color: Color = EMusicColor.primary

    /// Alto total, como el `Modifier.height(18.dp)` de Compose.
    var height: CGFloat = 18

    /// Fracciones inicial y final de cada barra, y su duración.
    private let bars: [(from: CGFloat, to: CGFloat, duration: Double)] = [
        (0.30, 1.00, 0.42),
        (1.00, 0.40, 0.52),
        (0.50, 0.90, 0.36),
    ]

    @State private var animating = false

    var body: some View {
        HStack(alignment: .bottom, spacing: 2.5) {
            ForEach(bars.indices, id: \.self) { i in
                let bar = bars[i]
                RoundedRectangle(cornerRadius: 2)
                    .fill(color)
                    .frame(width: 3, height: height * (animating ? bar.to : bar.from))
                    .animation(
                        .easeInOut(duration: bar.duration).repeatForever(autoreverses: true),
                        value: animating
                    )
            }
        }
        .frame(height: height, alignment: .bottom)
        .onAppear { animating = true }
    }
}
