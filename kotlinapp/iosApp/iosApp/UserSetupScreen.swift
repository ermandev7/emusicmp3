import SwiftUI

/// Puerto de `ui/setup/UserSetupScreen.kt`. Es la primera pantalla si todavia no hay
/// usuario guardado, igual que el gate de `MainActivity` en Android.
///
/// El backend identifica a la persona con el header `X-User-Id`, y de ese id cuelgan el
/// historial, los favoritos y las recomendaciones. Escribiendo aca el mismo nombre que se
/// usa en el telefono Android, el iPhone ve exactamente la misma biblioteca.
struct UserSetupScreen: View {

    @ObservedObject var session: UserSession

    @State private var userName = ""
    @FocusState private var fieldFocused: Bool

    var body: some View {
        ZStack {
            EMusicColor.background.ignoresSafeArea()

            VStack(spacing: 0) {
                Image(systemName: "music.note")
                    .font(.system(size: 80))
                    .foregroundStyle(EMusicColor.primary)

                Text("Bienvenido a eMusic")
                    .font(.title.bold())
                    .foregroundStyle(EMusicColor.onSurface)
                    .padding(.top, 24)

                Text("Ingresa un nombre de usuario para personalizar tu experiencia")
                    .font(.subheadline)
                    .foregroundStyle(EMusicColor.onSurfaceVariant)
                    .multilineTextAlignment(.center)
                    .padding(.top, 8)

                TextField("", text: $userName, prompt:
                    Text("Tu nombre").foregroundColor(EMusicColor.onSurfaceVariant)
                )
                .focused($fieldFocused)
                .foregroundStyle(EMusicColor.onSurface)
                .textInputAutocapitalization(.never)
                .autocorrectionDisabled()
                .submitLabel(.done)
                .onSubmit(start)
                .padding(.horizontal, 14)
                .padding(.vertical, 14)
                .background(EMusicColor.surfaceVariant)
                .clipShape(RoundedRectangle(cornerRadius: 8))
                .overlay(
                    RoundedRectangle(cornerRadius: 8)
                        .stroke(EMusicColor.outline, lineWidth: 1)
                )
                .padding(.top, 32)

                Button(action: start) {
                    Text("Comenzar")
                        .font(.body.weight(.semibold))
                        .frame(maxWidth: .infinity)
                        .padding(.vertical, 14)
                        .background(EMusicColor.primary)
                        .foregroundStyle(EMusicColor.onPrimary)
                        .clipShape(Capsule())
                }
                .padding(.top, 16)

                // Esto no esta en Android, pero aca hace falta decirlo: si el nombre no
                // coincide con el del telefono, el iPhone arranca con todo vacio.
                Text("Usa el mismo nombre que en tu Android para ver ahí tu historial, tus favoritos y tus recomendaciones.")
                    .font(.footnote)
                    .foregroundStyle(EMusicColor.onSurfaceVariant.opacity(0.8))
                    .multilineTextAlignment(.center)
                    .padding(.top, 20)
            }
            .padding(32)
        }
        .onAppear { fieldFocused = true }
    }

    private func start() {
        session.setUser(userName)
    }
}
