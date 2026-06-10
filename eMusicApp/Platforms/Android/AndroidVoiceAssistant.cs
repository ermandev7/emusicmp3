using Android.Content;
using eMusicApp.Services;

namespace eMusicApp.Platforms.Android;

/// <summary>
/// Implementación Android de IVoiceAssistant.
/// Puente entre el DI de MAUI y el VoiceListenerService nativo.
/// </summary>
public class AndroidVoiceAssistant : IVoiceAssistant
{
    public bool IsListening => VoiceListenerService.IsRunning;

    public event Action<VoiceCommand>? OnCommandRecognized;
    public event Action<string>? OnPartialResult;
    public event Action<string>? OnStatusChanged;

    public AndroidVoiceAssistant()
    {
        // Puentear eventos estáticos del service → instancia DI
        VoiceListenerService.OnCommandRecognized += cmd => OnCommandRecognized?.Invoke(cmd);
        VoiceListenerService.OnPartialResult += text => OnPartialResult?.Invoke(text);
        VoiceListenerService.OnStatusChanged += status => OnStatusChanged?.Invoke(status);
    }

    public void StartListening()
    {
        if (VoiceListenerService.IsRunning) return;

        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(VoiceListenerService));
        context.StartForegroundService(intent);
        Logger.Log("[VoiceAssistant] StartListening requested");
    }

    public void StopListening()
    {
        var context = global::Android.App.Application.Context;
        var intent = new Intent(context, typeof(VoiceListenerService));
        intent.SetAction("STOP");
        context.StartService(intent);
        Logger.Log("[VoiceAssistant] StopListening requested");
    }
}
