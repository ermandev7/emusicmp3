import SwiftUI

/// Puerto de `ui/components/Skeleton.kt`: placeholders con barrido de brillo para
/// mientras cargan las listas. Se usan en Inicio, Buscar y Biblioteca, igual que en
/// Android.

/// Barrido de brillo infinito. En Compose es `rememberInfiniteTransition` con
/// `tween(1100, LinearEasing)` en bucle; aca es una animacion repetida sobre el
/// desplazamiento del gradiente.
private struct ShimmerModifier: ViewModifier {

    @State private var phase: CGFloat = -1

    /// Semitransparentes, para que los placeholders dejen entrever el fondo.
    private let base = EMusicColor.onSurfaceVariant.opacity(0.12)
    private let highlight = EMusicColor.onSurfaceVariant.opacity(0.28)

    func body(content: Content) -> some View {
        content
            .background(
                GeometryReader { geo in
                    LinearGradient(
                        colors: [base, highlight, base],
                        startPoint: .leading,
                        endPoint: .trailing
                    )
                    .frame(width: geo.size.width * 3)
                    .offset(x: phase * geo.size.width * 1.5)
                }
            )
            .onAppear {
                withAnimation(.linear(duration: 1.1).repeatForever(autoreverses: false)) {
                    phase = 1
                }
            }
    }
}

extension View {
    func shimmer() -> some View {
        modifier(ShimmerModifier())
    }
}

/// Caja con forma redondeada y shimmer, la pieza basica de los placeholders.
private struct SkeletonBox: View {
    var width: CGFloat?
    var height: CGFloat
    var radius: CGFloat

    var body: some View {
        RoundedRectangle(cornerRadius: radius)
            .fill(Color.clear)
            .frame(width: width, height: height)
            .shimmer()
            .clipShape(RoundedRectangle(cornerRadius: radius))
    }
}

/// Fila placeholder con la forma de `TrackItem`: caratula 56, titulo al 70% del ancho
/// y artista al 40%. Mismas medidas que `TrackRowSkeleton` en Compose.
struct TrackRowSkeleton: View {
    var body: some View {
        HStack(spacing: 12) {
            SkeletonBox(width: 56, height: 56, radius: 6)

            GeometryReader { geo in
                VStack(alignment: .leading, spacing: 8) {
                    SkeletonBox(width: geo.size.width * 0.7, height: 14, radius: 4)
                    SkeletonBox(width: geo.size.width * 0.4, height: 12, radius: 4)
                }
                .frame(height: geo.size.height, alignment: .center)
            }
            .frame(height: 34)
        }
        .padding(.horizontal, 16)
        .padding(.vertical, 8)
    }
}

/// Lista de filas placeholder. Android usa 8 por defecto y 5 en Inicio.
struct TrackListSkeleton: View {
    var count: Int = 8

    var body: some View {
        VStack(spacing: 0) {
            ForEach(0..<count, id: \.self) { _ in
                TrackRowSkeleton()
            }
        }
    }
}
