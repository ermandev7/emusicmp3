namespace eMusicApp.Services;

/// <summary>
/// Servicio de asistente de voz con wake word + comando.
/// Implementación Android usa SpeechRecognizer en modo continuo.
/// </summary>
public interface IVoiceAssistant
{
    bool IsListening { get; }
    void StartListening();
    void StopListening();

    /// <summary>Se dispara cuando un comando completo fue reconocido y parseado.</summary>
    event Action<VoiceCommand>? OnCommandRecognized;

    /// <summary>Texto parcial mientras se escucha (para UI de feedback).</summary>
    event Action<string>? OnPartialResult;

    /// <summary>Cambios de estado: "idle", "listening", "wake_word_detected", "processing", "error".</summary>
    event Action<string>? OnStatusChanged;
}

public enum VoiceAction
{
    Play,
    Pause,
    Resume,
    Stop,
    Next,
    Previous,
    Search,
    Unknown
}

public record VoiceCommand(
    VoiceAction Action,
    string Query,
    string? Artist,
    string? TargetApp);
