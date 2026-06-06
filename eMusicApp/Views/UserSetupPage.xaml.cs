namespace eMusicApp.Views;

public partial class UserSetupPage : ContentPage
{
    public UserSetupPage()
    {
        InitializeComponent();

        // Quitar underline nativo de Android en el Entry
        Microsoft.Maui.Handlers.EntryHandler.Mapper.AppendToMapping("NoUnderline", (handler, view) =>
        {
#if ANDROID
            handler.PlatformView.BackgroundTintList =
                Android.Content.Res.ColorStateList.ValueOf(Android.Graphics.Color.Transparent);
#endif
        });
    }

    private void OnEntryCompleted(object? sender, EventArgs e)
    {
        SaveAndContinue();
    }

    private void OnEntrarClicked(object? sender, EventArgs e)
    {
        SaveAndContinue();
    }

    private void SaveAndContinue()
    {
        var name = NameEntry.Text?.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            DisplayAlert("", "Escribe tu nombre para continuar", "OK");
            return;
        }

        // Generar ID unico y guardar
        var userId = System.Guid.NewGuid().ToString("N")[..12]; // 12 chars, suficiente para familia
        Preferences.Default.Set("user_id", userId);
        Preferences.Default.Set("user_name", name);

        // Configurar el header en ApiService
        Services.ApiService.SetUserId(userId);

        // Navegar a la app principal
        Application.Current!.Windows[0].Page = new AppShell();
    }
}
