import SwiftUI

@main
struct iOSApp: App {

    @StateObject private var session = UserSession.shared

    var body: some Scene {
        WindowGroup {
            Group {
                // Mismo gate que `MainActivity`: sin usuario guardado se arranca en la
                // pantalla de bienvenida, no en Inicio.
                if session.isConfigured {
                    ContentView()
                } else {
                    UserSetupScreen(session: session)
                }
            }
            // La app Android usa `darkColorScheme` fijo (Theme.kt), no sigue el
            // tema del sistema. iOS hace lo mismo para que se vean iguales.
            .preferredColorScheme(.dark)
        }
    }
}
