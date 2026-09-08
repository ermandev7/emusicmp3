import SwiftUI

@main
struct iOSApp: App {
    var body: some Scene {
        WindowGroup {
            ContentView()
                // La app Android usa `darkColorScheme` fijo (Theme.kt), no sigue el
                // tema del sistema. iOS hace lo mismo para que se vean iguales.
                .preferredColorScheme(.dark)
        }
    }
}
