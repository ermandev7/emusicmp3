import SwiftUI

/// Sistema de diseno de eMusic portado desde Compose. Las fuentes son:
///  - `ui/theme/Theme.kt`      -> paleta base (`EMusicColor`)
///  - `ui/home/HomeScreen.kt`  -> gradientes de genero y colores de chips (`EMusicGenre`)
///  - `ui/components/*.kt`     -> medidas de filas y mini reproductor (`EMusicMetrics`)
///
/// La app Android usa `darkColorScheme` fijo (no sigue el tema del sistema), asi que
/// iOS se fuerza a oscuro en `iOSApp.swift`. Los hex son identicos a los de Compose.

// MARK: - Paleta

enum EMusicColor {
    /// Verde tipo Spotify. Acento, boton grande de play, barra de progreso,
    /// titulo de la pista que esta sonando.
    static let primary = Color(hex: 0x1DB954)
    static let onPrimary = Color.black
    static let primaryContainer = Color(hex: 0x14833B)

    static let background = Color(hex: 0x121212)
    static let surface = Color(hex: 0x1E1E1E)
    static let surfaceVariant = Color(hex: 0x2A2A2A)

    static let onBackground = Color.white
    static let onSurface = Color.white
    /// Texto secundario: artista, duracion, iconos apagados, cabeceras de seccion.
    static let onSurfaceVariant = Color(hex: 0xB3B3B3)
    static let outline = Color(hex: 0x535353)

    /// Corazon de favorito activo (`PlayerScreen.kt`).
    static let favorite = Color(hex: 0xE91E63)
}

// MARK: - Generos

/// Las 8 tarjetas de "Escuchar por genero" del Home. El `query` va tal cual al
/// buscador, asi que iOS devuelve exactamente los mismos resultados que Android.
struct EMusicGenre: Identifiable {
    let name: String
    let emoji: String
    let query: String
    let color1: Color
    let color2: Color

    var id: String { name }

    /// Gradiente de la tarjeta, de color1 a color2.
    var gradient: LinearGradient {
        LinearGradient(
            colors: [color1, color2],
            startPoint: .leading,
            endPoint: .trailing
        )
    }

    static let all: [EMusicGenre] = [
        EMusicGenre(name: "Salsa", emoji: "💃",
                    query: "salsa éxitos Héctor Lavoe Marc Anthony Rubén Blades mix",
                    color1: Color(hex: 0xE53935), color2: Color(hex: 0xFF7043)),
        EMusicGenre(name: "Cumbia", emoji: "🎺",
                    query: "cumbia éxitos Los Ángeles Azules Grupo 5 mix bailable",
                    color1: Color(hex: 0x5E35B1), color2: Color(hex: 0xAB47BC)),
        EMusicGenre(name: "Rock", emoji: "🎸",
                    query: "rock clásico Aerosmith Guns N Roses Metallica Mago de Oz éxitos",
                    color1: Color(hex: 0xB71C1C), color2: Color(hex: 0x424242)),
        EMusicGenre(name: "Reggaetón", emoji: "🔥",
                    query: "reggaeton éxitos 2024 Bad Bunny Daddy Yankee Karol G mix",
                    color1: Color(hex: 0x8E24AA), color2: Color(hex: 0xE040FB)),
        EMusicGenre(name: "Electrónica", emoji: "🎧",
                    query: "electronic dance music éxitos David Guetta Martin Garrix Avicii mix",
                    color1: Color(hex: 0x00695C), color2: Color(hex: 0x26C6DA)),
        EMusicGenre(name: "Reggae", emoji: "🌴",
                    query: "reggae éxitos Bob Marley UB40 mix popular",
                    color1: Color(hex: 0x2E7D32), color2: Color(hex: 0x66BB6A)),
        EMusicGenre(name: "Bachata", emoji: "🌹",
                    query: "bachata éxitos Romeo Santos Aventura Prince Royce mix",
                    color1: Color(hex: 0xD81B60), color2: Color(hex: 0xF48FB1)),
        EMusicGenre(name: "Pop", emoji: "⭐",
                    query: "pop latino éxitos 2024 Shakira Luis Fonsi Sebastián Yatra mix",
                    color1: Color(hex: 0xFF6F00), color2: Color(hex: 0xFFCA28)),
    ]

    /// Color de los chips de "Tus generos favoritos". Mismo mapa que `HomeScreen.kt`;
    /// gris `outline` para un genero que no este en la lista.
    static func chipColor(for genre: String) -> Color {
        let key = genre.lowercased()
        let map: [String: UInt32] = [
            "salsa": 0xE53935, "bachata": 0xD81B60,
            "reggaeton": 0x8E24AA, "cumbia": 0x5E35B1,
            "rock": 0xB71C1C, "pop": 0xFF6F00,
            "electronic": 0x00695C, "reggae": 0x2E7D32,
            "rap": 0x424242, "trap": 0x6A1B9A,
            "balada": 0xAD1457, "ranchera": 0x4E342E,
            "corrido": 0x33691E, "vallenato": 0x1E88E5,
            "merengue": 0x3949AB, "jazz": 0x0277BD,
            "blues": 0x1565C0, "clasica": 0x5D4037,
            "kpop": 0xC2185B, "r&b": 0x7B1FA2,
        ]
        guard let hex = map[key] else { return EMusicColor.outline }
        return Color(hex: hex)
    }
}

// MARK: - Medidas

/// Tomadas de los composables para que las pantallas coincidan pixel a pixel.
enum EMusicMetrics {
    // TrackItem.kt
    static let trackThumbnailSize: CGFloat = 56
    static let trackThumbnailRadius: CGFloat = 4
    static let trackRowHorizontalPadding: CGFloat = 16
    static let trackRowVerticalPadding: CGFloat = 8
    static let trackRowSpacing: CGFloat = 12

    // MiniPlayer.kt
    static let miniPlayerThumbnailSize: CGFloat = 46
    static let miniPlayerThumbnailRadius: CGFloat = 8
    static let miniPlayerTopRadius: CGFloat = 14
    static let miniPlayerProgressHeight: CGFloat = 2

    // HomeScreen.kt
    static let genreCardSpacing: CGFloat = 8
    static let genreCardRadius: CGFloat = 12
    static let sectionHorizontalPadding: CGFloat = 16

    /// Espacio que hay que dejar abajo para que el mini reproductor no tape la lista.
    static let bottomContentInset: CGFloat = 100
}

// MARK: - Utilidades

extension Color {
    /// `Color(hex: 0x1DB954)`, para copiar los valores de Compose tal cual.
    init(hex: UInt32) {
        self.init(
            .sRGB,
            red: Double((hex >> 16) & 0xFF) / 255,
            green: Double((hex >> 8) & 0xFF) / 255,
            blue: Double(hex & 0xFF) / 255,
            opacity: 1
        )
    }
}
