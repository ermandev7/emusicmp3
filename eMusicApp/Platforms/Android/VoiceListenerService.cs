using Android.App;
using Android.Content;
using Android.OS;
using Android.Speech;
using eMusicApp.Services;
using System.Net.Http;
using System.Text.Json;

namespace eMusicApp.Platforms.Android;

/// <summary>
/// Foreground service que mantiene SpeechRecognizer activo en modo continuo.
/// Flujo: escucha → detecta wake word → captura comando → parsea → ejecuta → reinicia.
///
/// Usa Android SpeechRecognizer (Google) con EXTRA_PREFER_OFFLINE para reducir latencia.
/// Para wake word offline con menor consumo de batería, se puede reemplazar la fase de
/// detección por Picovoice Porcupine (requiere API key en https://picovoice.ai/).
/// </summary>
[Service(Exported = false, ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeMicrophone)]
public class VoiceListenerService : Service
{
    private const string CHANNEL_ID = "emusic_voice";
    private const int NOTIFICATION_ID = 1002;
    private const int RESTART_DELAY_MS = 300;
    private const int ERROR_RESTART_DELAY_MS = 2000;

    private SpeechRecognizer? _recognizer;
    private Handler? _handler;
    private bool _isListening;
    private bool _wakeWordDetected;
    private bool _destroyed;

    // ── Eventos estáticos para comunicar con MAUI (patrón NativeAudioController) ──
    public static event Action<VoiceCommand>? OnCommandRecognized;
    public static event Action<string>? OnPartialResult;
    public static event Action<string>? OnStatusChanged;
    public static bool IsRunning { get; private set; }

    public override void OnCreate()
    {
        base.OnCreate();
        _handler = new Handler(Looper.MainLooper!);
        CreateNotificationChannel();
        StartForeground(NOTIFICATION_ID, BuildNotification("Escuchando..."),
            global::Android.Content.PM.ForegroundService.TypeMicrophone);
        IsRunning = true;
        NotifyStatus("listening");
        Logger.Log("[Voice] Service created");
        StartRecognizer();
    }

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        if (intent?.Action == "STOP")
        {
            StopSelf();
            return StartCommandResult.NotSticky;
        }
        return StartCommandResult.Sticky;
    }

    public override IBinder? OnBind(Intent? intent) => null;

    // ══════════════════════════════════════════════
    //  SpeechRecognizer — inicio y reinicio continuo
    // ══════════════════════════════════════════════

    private void StartRecognizer()
    {
        if (_destroyed) return;

        _handler?.Post(() =>
        {
            try
            {
                if (_recognizer == null)
                {
                    if (!SpeechRecognizer.IsRecognitionAvailable(this))
                    {
                        Logger.Log("[Voice] SpeechRecognizer no disponible en este dispositivo");
                        NotifyStatus("error");
                        return;
                    }
                    _recognizer = SpeechRecognizer.CreateSpeechRecognizer(this);
                    _recognizer.SetRecognitionListener(new VoiceRecognitionListener(this));
                }

                var recognizerIntent = new Intent(RecognizerIntent.ActionRecognizeSpeech);
                recognizerIntent.PutExtra(RecognizerIntent.ExtraLanguageModel, RecognizerIntent.LanguageModelFreeForm);
                recognizerIntent.PutExtra(RecognizerIntent.ExtraLanguage, "es-ES");
                recognizerIntent.PutExtra(RecognizerIntent.ExtraLanguagePreference, "es-ES");
                recognizerIntent.PutExtra(RecognizerIntent.ExtraPartialResults, true);
                recognizerIntent.PutExtra(RecognizerIntent.ExtraMaxResults, 1);
                // Preferir reconocimiento offline (Android 12+) para ahorrar batería
                recognizerIntent.PutExtra("android.speech.extra.PREFER_OFFLINE", true);
                // Extender timeouts para no cortar tan rápido
                recognizerIntent.PutExtra(RecognizerIntent.ExtraSpeechInputCompleteSilenceLengthMillis, 3000L);
                recognizerIntent.PutExtra(RecognizerIntent.ExtraSpeechInputMinimumLengthMillis, 5000L);
                recognizerIntent.PutExtra(RecognizerIntent.ExtraSpeechInputPossiblyCompleteSilenceLengthMillis, 2000L);

                _recognizer.StartListening(recognizerIntent);
                _isListening = true;
            }
            catch (Exception ex)
            {
                Logger.Log("[Voice] Error starting recognizer", ex);
                ScheduleRestart(ERROR_RESTART_DELAY_MS);
            }
        });
    }

    private void ScheduleRestart(int delayMs)
    {
        if (_destroyed) return;
        _isListening = false;
        _handler?.PostDelayed(StartRecognizer, delayMs);
    }

    // ══════════════════════════════════════════════
    //  Procesamiento de resultados
    // ══════════════════════════════════════════════

    internal void HandlePartialResults(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        OnPartialResult?.Invoke(text);

        if (!_wakeWordDetected)
        {
            // Buscar wake word en el texto parcial
            var afterWake = VoiceCommandParser.ExtractAfterWakeWord(text);
            if (afterWake != null)
            {
                _wakeWordDetected = true;
                NotifyStatus("wake_word_detected");
                UpdateNotification("Comando detectado...");
                Logger.Log($"[Voice] Wake word detectado. After: '{afterWake}'");
            }
        }
    }

    internal void HandleFinalResult(string text)
    {
        Logger.Log($"[Voice] Final: '{text}' wakeDetected={_wakeWordDetected}");

        if (_wakeWordDetected)
        {
            _wakeWordDetected = false;

            // Extraer la parte después del wake word
            var commandText = VoiceCommandParser.ExtractAfterWakeWord(text) ?? text;

            if (!string.IsNullOrWhiteSpace(commandText))
            {
                NotifyStatus("processing");
                UpdateNotification("Procesando...");

                var command = VoiceCommandParser.Parse(commandText);
                Logger.Log($"[Voice] Comando: action={command.Action}, query='{command.Query}', artist='{command.Artist}', app='{command.TargetApp}'");

                ExecuteCommand(command);
            }

            NotifyStatus("listening");
            UpdateNotification("Escuchando...");
        }

        // Reiniciar escucha
        ScheduleRestart(RESTART_DELAY_MS);
    }

    internal void HandleError(SpeechRecognizerError error)
    {
        // Errores comunes:
        // NoMatch (7): no se reconoció nada — normal, reiniciar
        // SpeechTimeout (6): silencio prolongado — normal, reiniciar
        // Network* (2,5): sin red — reiniciar con delay
        // Busy (8): otro recognizer activo — reiniciar con delay

        var delay = error switch
        {
            SpeechRecognizerError.NoMatch or SpeechRecognizerError.SpeechTimeout => RESTART_DELAY_MS,
            SpeechRecognizerError.RecognizerBusy => ERROR_RESTART_DELAY_MS,
            _ => ERROR_RESTART_DELAY_MS
        };

        if (error != SpeechRecognizerError.NoMatch && error != SpeechRecognizerError.SpeechTimeout)
            Logger.Log($"[Voice] Error: {error}");

        _wakeWordDetected = false;
        ScheduleRestart(delay);
    }

    // ══════════════════════════════════════════════
    //  Ejecución de comandos
    // ══════════════════════════════════════════════

    private void ExecuteCommand(VoiceCommand command)
    {
        OnCommandRecognized?.Invoke(command);

        // Si el comando apunta a otra app, lanzar Intent externo
        if (!string.IsNullOrEmpty(command.TargetApp)
            && !command.TargetApp.Contains("emusic", StringComparison.OrdinalIgnoreCase)
            && !command.TargetApp.Contains("música", StringComparison.OrdinalIgnoreCase))
        {
            LaunchExternalApp(command);
            return;
        }

        // Comando interno para eMusicApp
        switch (command.Action)
        {
            case VoiceAction.Pause:
            case VoiceAction.Stop:
                NativeAudioController.RequestPause();
                break;

            case VoiceAction.Resume:
                NativeAudioController.RequestResume();
                break;

            case VoiceAction.Next:
                AndroidMedia3Service.Instance?.HandleNext();
                break;

            case VoiceAction.Previous:
                AndroidMedia3Service.Instance?.HandlePrev();
                break;

            case VoiceAction.Play:
            case VoiceAction.Search:
                if (!string.IsNullOrWhiteSpace(command.Query))
                    _ = SearchAndPlayAsync(command.Query);
                break;
        }
    }

    /// <summary>
    /// Busca en la API y reproduce el primer resultado.
    /// Reutiliza la misma lógica de MainActivity.HandleVoiceIntent.
    /// </summary>
    private async Task SearchAndPlayAsync(string query)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            var userId = Microsoft.Maui.Storage.Preferences.Default.Get("user_id", "");
            if (!string.IsNullOrEmpty(userId))
                http.DefaultRequestHeaders.Add("X-User-Id", userId);

            var searchUrl = $"{AppConstants.ApiBaseUrl}/search?q={Uri.EscapeDataString(query)}";
            var json = await http.GetStringAsync(searchUrl);
            using var doc = JsonDocument.Parse(json);

            JsonElement items;
            if (doc.RootElement.TryGetProperty("items", out items)) { }
            else if (doc.RootElement.ValueKind == JsonValueKind.Array) items = doc.RootElement;
            else return;

            string? videoId = null, title = null, artist = null, thumb = null;
            foreach (var el in items.EnumerateArray())
            {
                if (el.TryGetProperty("type", out var typeEl))
                {
                    var type = typeEl.GetString();
                    if (!string.IsNullOrEmpty(type) && type != "stream") continue;
                }
                videoId = ExtractVideoId(el);
                if (string.IsNullOrEmpty(videoId)) continue;
                title = el.TryGetProperty("title", out var tp) ? tp.GetString() ?? "" : "";
                artist = el.TryGetProperty("uploaderName", out var up) ? up.GetString() ?? ""
                       : el.TryGetProperty("uploader", out var up2) ? up2.GetString() ?? "" : "";
                thumb = el.TryGetProperty("thumbnailUrl", out var thp) ? thp.GetString() ?? ""
                      : el.TryGetProperty("thumbnail", out var thp2) ? thp2.GetString() ?? "" : "";
                break;
            }

            if (string.IsNullOrEmpty(videoId)) return;

            var streamJson = await http.GetStringAsync($"{AppConstants.ApiBaseUrl}/streams/{videoId}");
            using var streamDoc = JsonDocument.Parse(streamJson);
            string? bestUrl = null;
            int bestBitrate = 0;
            foreach (var s in streamDoc.RootElement.GetProperty("audioStreams").EnumerateArray())
            {
                int br = s.TryGetProperty("bitrate", out var brEl) ? brEl.GetInt32() : 0;
                string? su = s.TryGetProperty("url", out var suEl) ? suEl.GetString() : null;
                if (su != null && br > bestBitrate) { bestBitrate = br; bestUrl = su; }
            }

            if (string.IsNullOrEmpty(bestUrl)) return;

            Logger.Log($"[Voice] Playing: {title} by {artist}");
            NativeAudioController.RequestPlay(bestUrl, title!, artist!, thumb!, videoId);
        }
        catch (Exception ex)
        {
            Logger.Log($"[Voice] SearchAndPlay error", ex);
        }
    }

    /// <summary>
    /// Lanza un Intent MediaPlayFromSearch para que otra app reproduzca la query.
    /// Ejemplo: "pon rock en spotify" → lanza intent hacia Spotify.
    /// </summary>
    private void LaunchExternalApp(VoiceCommand command)
    {
        try
        {
            var intent = new Intent(global::Android.Provider.MediaStore.IntentActionMediaPlayFromSearch);
            intent.PutExtra(global::Android.App.SearchManager.Query, command.Query);
            intent.PutExtra(global::Android.Provider.MediaStore.ExtraMediaFocus, "vnd.android.cursor.item/*");
            intent.AddFlags(ActivityFlags.NewTask);

            // Intentar resolver el nombre de la app a un package
            // ── Aquí puedes agregar un mapeo de nombres comunes a package names ──
            //    Ej: "spotify" → "com.spotify.music"
            //        "youtube" → "com.google.android.youtube"
            var packageName = ResolvePackageName(command.TargetApp!);
            if (packageName != null)
                intent.SetPackage(packageName);

            StartActivity(intent);
            Logger.Log($"[Voice] Launched external: app={command.TargetApp}, query={command.Query}");
        }
        catch (Exception ex)
        {
            Logger.Log($"[Voice] Error launching external app", ex);
        }
    }

    /// <summary>
    /// Mapea nombres hablados a package names de Android.
    /// ── Agrega aquí las apps que uses ──
    /// </summary>
    private static string? ResolvePackageName(string appName)
    {
        var lower = appName.ToLowerInvariant().Trim();
        return lower switch
        {
            "spotify" => "com.spotify.music",
            "youtube" or "youtube music" => "com.google.android.apps.youtube.music",
            "deezer" => "deezer.android.app",
            "tidal" => "com.aspiro.tidal",
            "amazon music" or "amazon" => "com.amazon.mp3",
            _ => null // No se conoce el paquete, el sistema resolverá
        };
    }

    private static string? ExtractVideoId(JsonElement el)
    {
        if (el.TryGetProperty("videoId", out var vp))
        {
            var v = vp.GetString();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        if (el.TryGetProperty("url", out var urlP))
        {
            var u = urlP.GetString() ?? "";
            var idx = u.IndexOf("?v=");
            if (idx >= 0)
            {
                var id = u.Substring(idx + 3);
                var ampIdx = id.IndexOf('&');
                return ampIdx >= 0 ? id.Substring(0, ampIdx) : id;
            }
        }
        return null;
    }

    // ══════════════════════════════════════════════
    //  Notificación del servicio de voz
    // ══════════════════════════════════════════════

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.O) return;
        var nm = (NotificationManager)GetSystemService(NotificationService)!;
        nm.DeleteNotificationChannel(CHANNEL_ID);
        var channel = new NotificationChannel(CHANNEL_ID, "eMusicApp - Asistente de voz",
            NotificationImportance.Low)
        {
            Description = "Escucha continua para comandos de voz"
        };
        channel.SetShowBadge(false);
        channel.SetSound(null, null);
        channel.EnableVibration(false);
        nm.CreateNotificationChannel(channel);
    }

    private Notification BuildNotification(string text)
    {
        var launchIntent = PackageManager?.GetLaunchIntentForPackage(PackageName ?? "");
        PendingIntent? contentIntent = null;
        if (launchIntent != null)
            contentIntent = PendingIntent.GetActivity(this, 0, launchIntent,
                PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        // Botón para detener la escucha
        var stopIntent = new Intent(this, Java.Lang.Class.FromType(typeof(VoiceListenerService)));
        stopIntent.SetAction("STOP");
        var stopPending = PendingIntent.GetForegroundService(this, 100, stopIntent,
            PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable);

        var builder = new Notification.Builder(this, CHANNEL_ID)
            .SetContentTitle("Asistente de voz")
            .SetContentText(text)
            .SetSmallIcon(Resource.Mipmap.appicon)
            .SetOngoing(true)
            .SetVisibility(NotificationVisibility.Public)
            .AddAction(new Notification.Action.Builder(
                global::Android.Resource.Drawable.IcMenuCloseClearCancel,
                "Detener", stopPending).Build());

        if (contentIntent != null)
            builder.SetContentIntent(contentIntent);

        return builder.Build();
    }

    private void UpdateNotification(string text)
    {
        try
        {
            var nm = (NotificationManager?)GetSystemService(NotificationService);
            nm?.Notify(NOTIFICATION_ID, BuildNotification(text));
        }
        catch { }
    }

    private static void NotifyStatus(string status)
        => OnStatusChanged?.Invoke(status);

    // ══════════════════════════════════════════════
    //  Cleanup
    // ══════════════════════════════════════════════

    public override void OnDestroy()
    {
        _destroyed = true;
        IsRunning = false;
        _handler?.RemoveCallbacksAndMessages(null);

        try
        {
            _recognizer?.StopListening();
            _recognizer?.Cancel();
            _recognizer?.Destroy();
        }
        catch { }

        _recognizer = null;
        _handler = null;
        NotifyStatus("idle");
        Logger.Log("[Voice] Service destroyed");
        base.OnDestroy();
    }

    // ══════════════════════════════════════════════
    //  RecognitionListener interno
    // ══════════════════════════════════════════════

    private class VoiceRecognitionListener : Java.Lang.Object, IRecognitionListener
    {
        private readonly VoiceListenerService _service;

        public VoiceRecognitionListener(VoiceListenerService service) => _service = service;

        public void OnReadyForSpeech(Bundle? @params) { }
        public void OnBeginningOfSpeech() { }
        public void OnRmsChanged(float rmsdB) { }
        public void OnBufferReceived(byte[]? buffer) { }
        public void OnEndOfSpeech() { }

        public void OnPartialResults(Bundle? partialResults)
        {
            var matches = partialResults?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
            if (matches?.Count > 0)
                _service.HandlePartialResults(matches[0]!);
        }

        public void OnResults(Bundle? results)
        {
            var matches = results?.GetStringArrayList(SpeechRecognizer.ResultsRecognition);
            if (matches?.Count > 0)
                _service.HandleFinalResult(matches[0]!);
            else
                _service.ScheduleRestart(RESTART_DELAY_MS);
        }

        public void OnError(SpeechRecognizerError error)
            => _service.HandleError(error);

        public void OnEvent(int eventType, Bundle? @params) { }

        // Overloads para API 34+
        public void OnSegmentResults(Bundle results) { }
        public void OnEndOfSegmentedSession() { }
        public void OnLanguageDetection(Bundle results) { }
    }
}
