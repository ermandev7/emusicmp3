import Foundation
import Network

/// Saber si hay red, para no quedarse esperando cuando no la hay.
///
/// **Por qué hace falta.** Sin esto, estando sin conexión cada pantalla lanzaba igual sus
/// peticiones y se quedaba con el esqueleto de carga puesto hasta que Ktor agotaba su
/// tiempo de espera — varios segundos por llamada, y en Biblioteca eran tres seguidas. El
/// efecto para el usuario era que la app "se colgaba" y no se podía ni llegar a Descargas,
/// que es justo lo único que funciona sin internet.
///
/// Android no tiene equivalente porque allí no hace falta del mismo modo: la pestaña de
/// descargas lee de MediaStore y nunca dependió de la red.
@MainActor
final class Connectivity: ObservableObject {

    static let shared = Connectivity()

    /// Empieza en `true` a propósito: hasta que el monitor diga lo contrario, se asume que
    /// hay red. Al revés se vería un "sin conexión" falso durante el arranque.
    @Published private(set) var isOnline = true

    private let monitor = NWPathMonitor()

    private init() {
        monitor.pathUpdateHandler = { [weak self] path in
            Task { @MainActor in
                self?.isOnline = path.status == .satisfied
            }
        }
        monitor.start(queue: DispatchQueue(label: "com.emusic.connectivity"))
    }
}
