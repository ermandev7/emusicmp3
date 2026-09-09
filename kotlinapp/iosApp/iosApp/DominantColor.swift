import SwiftUI
import UIKit

/// Puerto de `ui/player/DominantColor.kt`. En Android se usa la libreria Palette de
/// AndroidX y se pide su `darkVibrantSwatch`; en iOS no existe equivalente de sistema,
/// asi que se reimplementa el mismo criterio a mano: reducir la caratula a una rejilla
/// pequeña, agrupar los pixeles por tono y quedarse con el mas vivo y oscuro.
///
/// El resultado se usa para el gradiente de fondo de la pantalla de reproductor.
enum DominantColor {

    /// Tamaño al que se reduce la imagen antes de analizarla. Android le pide 180 px a
    /// Palette; 32x32 (1024 pixeles) da el mismo color y es practicamente gratis.
    private static let sampleSize = 32

    /// Cache en memoria: la misma caratula se pide al volver a abrir el reproductor.
    private static var cache: [String: Color] = [:]

    static func extract(from urlString: String) async -> Color? {
        if let hit = cache[urlString] { return hit }
        guard let url = URL(string: urlString),
              let (data, _) = try? await URLSession.shared.data(from: url),
              let image = UIImage(data: data) else { return nil }

        guard let color = await Task.detached(priority: .utility, operation: {
            analyze(image)
        }).value else { return nil }

        cache[urlString] = color
        return color
    }

    // MARK: - Analisis

    private static func analyze(_ image: UIImage) -> Color? {
        guard let pixels = downsample(image) else { return nil }

        // Se agrupan los pixeles en cubos de color (5 bits por canal) para que las
        // variaciones de compresion no cuenten como colores distintos.
        var buckets: [Int: (count: Int, r: Int, g: Int, b: Int)] = [:]
        for i in stride(from: 0, to: pixels.count, by: 4) {
            let r = Int(pixels[i]), g = Int(pixels[i + 1]), b = Int(pixels[i + 2])
            let alpha = pixels[i + 3]
            guard alpha > 128 else { continue }

            let key = (r >> 3) << 10 | (g >> 3) << 5 | (b >> 3)
            let current = buckets[key] ?? (0, 0, 0, 0)
            buckets[key] = (current.count + 1, current.r + r, current.g + g, current.b + b)
        }
        guard !buckets.isEmpty else { return nil }

        // Se puntua cada grupo como hace `darkVibrantSwatch`: mucha saturacion y
        // luminosidad tirando a baja, pesado por cuantos pixeles lo forman.
        var best: (score: Double, color: UIColor)?
        let total = Double(pixels.count / 4)

        for (_, bucket) in buckets {
            let r = Double(bucket.r) / Double(bucket.count) / 255
            let g = Double(bucket.g) / Double(bucket.count) / 255
            let b = Double(bucket.b) / Double(bucket.count) / 255

            let maxC = max(r, g, b), minC = min(r, g, b)
            let lightness = (maxC + minC) / 2
            let saturation = maxC == minC
                ? 0
                : (maxC - minC) / (lightness > 0.5 ? (2 - maxC - minC) : (maxC + minC))

            // Los grises y los extremos (casi negro o casi blanco) no sirven de fondo.
            guard saturation > 0.2, lightness > 0.08, lightness < 0.8 else { continue }

            let population = Double(bucket.count) / total
            // Objetivo de luminosidad 0.3: es lo que hace "dark vibrant".
            let darkness = 1 - abs(lightness - 0.3) / 0.5
            let score = saturation * 0.6 + max(0, darkness) * 0.25 + population * 0.15

            if best == nil || score > best!.score {
                best = (score, UIColor(red: r, green: g, blue: b, alpha: 1))
            }
        }

        // Si la caratula es practicamente monocroma no hay "vibrant": se usa el color
        // mas repetido, que es lo que hace `dominantSwatch` como ultimo recurso.
        if best == nil, let top = buckets.max(by: { $0.value.count < $1.value.count })?.value {
            best = (0, UIColor(
                red: Double(top.r) / Double(top.count) / 255,
                green: Double(top.g) / Double(top.count) / 255,
                blue: Double(top.b) / Double(top.count) / 255,
                alpha: 1
            ))
        }

        guard let color = best?.color else { return nil }
        return Color(uiColor: color)
    }

    /// Redibuja la imagen a `sampleSize` x `sampleSize` en RGBA y devuelve los bytes.
    private static func downsample(_ image: UIImage) -> [UInt8]? {
        guard let cgImage = image.cgImage else { return nil }

        let side = sampleSize
        var pixels = [UInt8](repeating: 0, count: side * side * 4)

        guard let context = CGContext(
            data: &pixels,
            width: side,
            height: side,
            bitsPerComponent: 8,
            bytesPerRow: side * 4,
            space: CGColorSpaceCreateDeviceRGB(),
            bitmapInfo: CGImageAlphaInfo.premultipliedLast.rawValue
        ) else { return nil }

        context.interpolationQuality = .low
        context.draw(cgImage, in: CGRect(x: 0, y: 0, width: side, height: side))
        return pixels
    }
}

/// Envoltorio para SwiftUI: expone el color dominante ya animado, como hace
/// `rememberDominantColor` con `animateColorAsState(tween(700))` en Compose.
@MainActor
final class DominantColorLoader: ObservableObject {

    @Published private(set) var color: Color

    private let fallback: Color
    private var currentURL: String?

    init(fallback: Color = EMusicColor.surfaceVariant) {
        self.fallback = fallback
        self.color = fallback
    }

    /// Se le pasa la miniatura ORIGINAL (no maxresdefault): siempre existe, mientras
    /// que la de maxima calidad puede dar 404 y dejaria el fondo sin color. Es
    /// exactamente el mismo criterio que el comentario de `PlayerScreen.kt`.
    func load(from urlString: String?) {
        guard let urlString, !urlString.isEmpty else {
            color = fallback
            currentURL = nil
            return
        }
        guard urlString != currentURL else { return }
        currentURL = urlString

        Task {
            let extracted = await DominantColor.extract(from: urlString)
            guard self.currentURL == urlString else { return }
            withAnimation(.easeInOut(duration: 0.7)) {
                self.color = extracted ?? self.fallback
            }
        }
    }
}
