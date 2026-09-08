import Foundation
import shared

/// Fase 5, punto 5: el equivalente iOS del DataStore de Android (key `user_id`).
///
/// El backend identifica al usuario con el header `X-User-Id` en cada request; no hay
/// login todavia, asi que alcanza con un identificador estable por dispositivo. Se
/// genera una sola vez y queda en UserDefaults.
enum UserIdStore {
    private static let key = "user_id"

    /// Identificador del usuario. Se genera en el primer acceso y ya no cambia.
    static var current: String {
        if let saved = UserDefaults.standard.string(forKey: key), !saved.isEmpty {
            return saved
        }
        let generated = UUID().uuidString
        UserDefaults.standard.set(generated, forKey: key)
        return generated
    }

    /// Permite fijar un userId existente (por ejemplo el mismo que ya usa el telefono
    /// Android, para compartir favoritos e historial entre dispositivos).
    static func set(_ value: String) {
        UserDefaults.standard.set(value, forKey: key)
    }
}

/// Adaptador del parametro `userIdProvider: suspend () -> String` de `MusicApiClient`.
///
/// Kotlin/Native exporta los tipos de funcion `suspend` como el protocolo
/// `KotlinSuspendFunction0`, cuyo unico metodo entrega el resultado por completion
/// handler. Como leer UserDefaults es sincronico, se responde en el acto.
final class UserIdProvider: NSObject, KotlinSuspendFunction0 {
    func invoke(completionHandler: @escaping (Any?, Error?) -> Void) {
        completionHandler(UserIdStore.current, nil)
    }
}
