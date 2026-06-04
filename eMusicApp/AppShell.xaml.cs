using eMusicApp.Views;

namespace eMusicApp;

public partial class AppShell : Shell
{
	public AppShell()
	{
		InitializeComponent();
		Routing.RegisterRoute("QueuePage", typeof(QueuePage));
	}
}
