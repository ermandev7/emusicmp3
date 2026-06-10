using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Provider;

namespace eMusicApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode
        | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter(new[] { MediaStore.IntentActionMediaPlayFromSearch },
    Categories = new[] { Intent.CategoryDefault })]
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

        // Permisos en runtime: notificaciones (Android 13+) y micrófono (asistente de voz)
        var permissionsNeeded = new System.Collections.Generic.List<string>();

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu
            && CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            permissionsNeeded.Add(global::Android.Manifest.Permission.PostNotifications);

        if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
            permissionsNeeded.Add(global::Android.Manifest.Permission.RecordAudio);

        if (permissionsNeeded.Count > 0)
            RequestPermissions(permissionsNeeded.ToArray(), 1);

        // Start the service so it's alive and listening to NativeAudioController
        try
        {
            var intent = new Intent(this, typeof(Platforms.Android.AndroidMedia3Service));
            StartForegroundService(intent);
            Logger.Log("AndroidMedia3Service started from MainActivity.");
        }
        catch (Exception ex)
        {
            Logger.Log("Failed to start AndroidMedia3Service from MainActivity.", ex);
        }

        // Si la Activity se abrió con un intent de voz, procesarlo
        HandleVoiceIntent(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        if (intent != null)
            HandleVoiceIntent(intent);
    }

    /// <summary>
    /// Procesa intents de Google Assistant tipo "reproduce X en eMusicApp".
    /// Extrae la query y la envía al servicio de Media3 vía NativeAudioController.
    /// </summary>
    private void HandleVoiceIntent(Intent? intent)
    {
        if (intent?.Action != MediaStore.IntentActionMediaPlayFromSearch) return;

        var query = intent.GetStringExtra(SearchManager.Query);
        Logger.Log($"Voice intent received: query='{query}'");

        if (string.IsNullOrWhiteSpace(query)) return;

        // Enviar la búsqueda al servicio como si fuera un MediaItem con searchQuery
        Task.Run(async () =>
        {
            try
            {
                var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(15) };
                var userId = Microsoft.Maui.Storage.Preferences.Default.Get("user_id", "");
                if (!string.IsNullOrEmpty(userId))
                    http.DefaultRequestHeaders.Add("X-User-Id", userId);

                // Buscar en la API
                var searchUrl = $"{AppConstants.ApiBaseUrl}/search?q={Uri.EscapeDataString(query)}";
                var json = await http.GetStringAsync(searchUrl);
                using var doc = System.Text.Json.JsonDocument.Parse(json);

                System.Text.Json.JsonElement items;
                if (doc.RootElement.TryGetProperty("items", out items)) { }
                else if (doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Array) items = doc.RootElement;
                else return;

                string? videoId = null, title = null, artist = null, thumb = null;
                foreach (var el in items.EnumerateArray())
                {
                    if (el.TryGetProperty("type", out var typeEl))
                    {
                        var type = typeEl.GetString();
                        if (!string.IsNullOrEmpty(type) && type != "stream") continue;
                    }
                    videoId = el.TryGetProperty("videoId", out var vp) ? vp.GetString() : null;
                    if (string.IsNullOrEmpty(videoId) && el.TryGetProperty("url", out var urlP))
                    {
                        var u = urlP.GetString() ?? "";
                        var idx = u.IndexOf("?v=");
                        if (idx >= 0)
                        {
                            videoId = u.Substring(idx + 3);
                            var amp = videoId.IndexOf('&');
                            if (amp >= 0) videoId = videoId.Substring(0, amp);
                        }
                    }
                    if (string.IsNullOrEmpty(videoId)) continue;
                    title = el.TryGetProperty("title", out var tp) ? tp.GetString() ?? "" : "";
                    artist = el.TryGetProperty("uploaderName", out var up) ? up.GetString() ?? ""
                           : el.TryGetProperty("uploader", out var up2) ? up2.GetString() ?? "" : "";
                    thumb = el.TryGetProperty("thumbnailUrl", out var thp) ? thp.GetString() ?? ""
                          : el.TryGetProperty("thumbnail", out var thp2) ? thp2.GetString() ?? "" : "";
                    break;
                }

                if (string.IsNullOrEmpty(videoId)) return;

                // Obtener stream URL
                var streamJson = await http.GetStringAsync($"{AppConstants.ApiBaseUrl}/streams/{videoId}");
                using var streamDoc = System.Text.Json.JsonDocument.Parse(streamJson);
                string? bestUrl = null;
                int bestBitrate = 0;
                foreach (var s in streamDoc.RootElement.GetProperty("audioStreams").EnumerateArray())
                {
                    int br = s.TryGetProperty("bitrate", out var brEl) ? brEl.GetInt32() : 0;
                    string? su = s.TryGetProperty("url", out var suEl) ? suEl.GetString() : null;
                    if (su != null && br > bestBitrate) { bestBitrate = br; bestUrl = su; }
                }

                if (string.IsNullOrEmpty(bestUrl)) return;

                Logger.Log($"Voice intent playing: {title} by {artist}");
                NativeAudioController.RequestPlay(bestUrl, title!, artist!, thumb!, videoId);
            }
            catch (Exception ex)
            {
                Logger.Log($"Voice intent error: {ex.Message}");
            }
        });
    }
}
