import Foundation
import shared

/// Cableado de los clientes del modulo compartido. Es el equivalente iOS del
/// `NetworkModule` de Hilt en Android: arma una sola vez la cadena
/// HttpClient -> MusicApiClient -> PipedFallbackClient -> EMusicNetworkClient,
/// mas el `RadioEngine` que ya viene resuelto en `shared`.
///
/// Nada de esto es codigo nuevo de red: toda la logica (endpoints, fallback a las
/// instancias publicas de Piped, modo radio) vive en Kotlin y la comparten Android e iOS.
final class SharedClients {

    static let shared = SharedClients()

    /// Cliente directo del backend propio (favoritos, historial, playlists, recomendaciones).
    let api: MusicApiClient

    /// Backend propio + fallback automatico a Piped para `search` y `getStream`.
    let network: EMusicNetworkClient

    /// Modo radio: extiende la cola sola con temas del mismo artista y, si se agotan,
    /// con recomendadas. La logica ya esta testeada en `commonTest`.
    let radio: RadioEngine

    private init() {
        // El motor HTTP lo elige Ktor segun la plataforma: Darwin en iOS, OkHttp en Android.
        let httpClient = HttpClientFactoryKt.createEMusicHttpClient()
        let fallbackHttpClient = PipedFallbackClientKt.createPipedFallbackHttpClient()

        let api = MusicApiClient(
            httpClient: httpClient,
            baseUrl: "http://emusicmp3.duckdns.org:5050/api",
            userIdProvider: UserIdProvider()
        )
        let fallback = PipedFallbackClient(httpClient: fallbackHttpClient)
        let network = EMusicNetworkClient(api: api, fallback: fallback)

        self.api = api
        self.network = network
        self.radio = RadioEngine(source: NetworkRadioSource(network: network, api: api))
    }
}
