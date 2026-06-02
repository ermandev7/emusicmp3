using Android.App;
using Android.Content.PM;
using Android.OS;

namespace eMusicApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            Logger.Log("FATAL UNHANDLED EXCEPTION", e.ExceptionObject as Exception);
        };

        TaskScheduler.UnobservedTaskException += (s, e) =>
        {
            Logger.Log("FATAL UNOBSERVED TASK EXCEPTION", e.Exception);
        };

        Logger.Log("MainActivity OnCreate started.");

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            {
                RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, 1);
            }
        }

        // Start the service so it's alive and listening to NativeAudioController
        try
        {
            var intent = new Android.Content.Intent(this, typeof(Platforms.Android.AndroidMedia3Service));
            // Usar StartService (no StartForegroundService) — MediaSessionService llama
            // startForeground() automáticamente cuando empieza la reproducción.
            // StartForegroundService causaría crash si no hay reproducción en los primeros 5 segundos.
            StartService(intent);
            Logger.Log("AndroidMedia3Service started from MainActivity.");
        }
        catch (Exception ex)
        {
            Logger.Log("Failed to start AndroidMedia3Service from MainActivity.", ex);
        }
    }
}
