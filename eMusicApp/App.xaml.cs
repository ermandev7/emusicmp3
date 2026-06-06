namespace eMusicApp;

public partial class App : Application
{
	public App()
	{
		InitializeComponent();
	}

	protected override Window CreateWindow(IActivationState? activationState)
	{
		var userId = Preferences.Default.Get("user_id", "");

		if (string.IsNullOrEmpty(userId))
		{
			// Primera vez: pedir nombre
			return new Window(new Views.UserSetupPage());
		}

		// Ya tiene usuario: configurar header y entrar
		Services.ApiService.SetUserId(userId);
		return new Window(new AppShell());
	}
}
