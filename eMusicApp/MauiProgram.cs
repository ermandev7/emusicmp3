using Microsoft.Extensions.Logging;
using eMusicApp.Services;
using eMusicApp.ViewModels;
using eMusicApp.Views;
using Maui.Skeleton;

namespace eMusicApp;

public static class MauiProgram
{
	public static MauiApp CreateMauiApp()
	{
		var builder = MauiApp.CreateBuilder();
		builder
			.UseMauiApp<App>()
			.ConfigureFonts(fonts =>
			{
				fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
				fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
			});

#if DEBUG
		builder.Logging.AddDebug();
#endif

		// Services
		builder.Services.AddSingleton<ApiService>();
		builder.Services.AddSingleton<PlayerViewModel>();

		// ViewModels
		builder.Services.AddTransient<HomeViewModel>();
		builder.Services.AddTransient<SearchViewModel>();
		builder.Services.AddTransient<LibraryViewModel>();

		// Pages
		builder.Services.AddTransient<HomePage>();
		builder.Services.AddTransient<SearchPage>();
		builder.Services.AddTransient<LibraryPage>();
		builder.Services.AddTransient<FullPlayerPage>();

		return builder.Build();
	}
}
