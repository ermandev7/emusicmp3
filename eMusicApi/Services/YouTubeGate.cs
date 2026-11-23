using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace eMusicApi.Services;

/// <summary>
/// Control de volumen hacia YouTube. Toda extracción con yt-dlp pasa por aquí.
///
/// Motivo: YouTube bloquea la IP de la Pi ("429 Too Many Requests" / "Sign in to
/// confirm you're not a bot") cuando recibe una ráfaga de extracciones. Pasó el
/// 29/09, el 22/10 y el 23/11/2026 (27 canciones extraídas en un minuto, cada una
/// con varias peticiones). Mientras dura el bloqueo NINGUNA canción reproduce.
///
/// Tres protecciones:
///  1. Tope por minuto (ventana deslizante). Lo que el usuario pulsa tiene prioridad:
///     el precalentado solo puede gastar una parte del cupo y se descarta si no cabe.
///  2. Tope de procesos yt-dlp simultáneos (la Pi no lanza decenas a la vez).
///  3. Cortacircuitos: si YouTube responde con un bloqueo, se deja de llamarle
///     durante unos minutos (cada vez más si se repite) en vez de seguir insistiendo,
///     que es lo que alargaba el bloqueo.
/// </summary>
public sealed class YouTubeGate
{
    private readonly int _maxPerWindow;
    private readonly int _prefetchBudget;
    private readonly TimeSpan _window;
    private readonly TimeSpan _maxPlayWait;
    private readonly TimeSpan _maxPrefetchWait;
    private readonly TimeSpan[] _backoff;

    private readonly object _lock = new();
    private readonly Queue<DateTime> _starts = new();   // inicio de cada extracción reciente
    private readonly SemaphoreSlim _slots;              // yt-dlp simultáneos (total)
    private readonly SemaphoreSlim _prefetchSlots;      // de ellos, cuántos puede ocupar el precalentado
    private DateTime _blockedUntil = DateTime.MinValue;
    private int _blockLevel;                            // índice en _backoff para el próximo bloqueo
    private bool _probing;                              // tras un bloqueo: sin precalentado hasta el primer éxito

    public YouTubeGate(
        int maxPerWindow = 12,
        int prefetchBudget = 6,
        int maxConcurrent = 3,
        int maxConcurrentPrefetch = 2,
        TimeSpan? window = null,
        TimeSpan? maxPlayWait = null,
        TimeSpan? maxPrefetchWait = null,
        TimeSpan[]? backoff = null)
    {
        _maxPerWindow = Math.Max(1, maxPerWindow);
        _prefetchBudget = Math.Clamp(prefetchBudget, 0, _maxPerWindow);
        _window = window ?? TimeSpan.FromMinutes(1);
        // La app corta la petición a los 25 s: esperar cupo como mucho 10 s deja
        // margen para la extracción en sí (4-8 s normalmente).
        _maxPlayWait = maxPlayWait ?? TimeSpan.FromSeconds(10);
        _maxPrefetchWait = maxPrefetchWait ?? TimeSpan.FromSeconds(6);
        _backoff = backoff is { Length: > 0 }
            ? backoff
            : new[] { TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(15) };

        var concurrent = Math.Max(1, maxConcurrent);
        var concurrentPrefetch = Math.Clamp(maxConcurrentPrefetch, 1, concurrent);
        _slots = new SemaphoreSlim(concurrent, concurrent);
        _prefetchSlots = new SemaphoreSlim(concurrentPrefetch, concurrentPrefetch);
    }

    /// <summary>
    /// Topes ajustables sin recompilar, con variables de entorno del contenedor:
    /// YT_MAX_PER_MINUTE (12) y YT_PREFETCH_PER_MINUTE (6).
    /// </summary>
    public static YouTubeGate FromEnvironment()
    {
        static int Env(string name, int fallback) =>
            int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

        var gate = new YouTubeGate(
            maxPerWindow: Env("YT_MAX_PER_MINUTE", 12),
            prefetchBudget: Env("YT_PREFETCH_PER_MINUTE", 6));
        Console.WriteLine(
            $"[Gate] Tope de extracciones: {gate._maxPerWindow}/min (precalentado hasta {gate._prefetchBudget}/min).");
        return gate;
    }

    /// <summary>Permiso para una extracción. Hay que liberarlo (Dispose) al terminar.</summary>
    public sealed class Lease : IDisposable
    {
        private YouTubeGate? _gate;
        private readonly bool _isPrefetch;

        internal Lease(YouTubeGate gate, bool isPrefetch)
        {
            _gate = gate;
            _isPrefetch = isPrefetch;
        }

        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _gate, null);
            if (gate == null) return;
            gate._slots.Release();
            if (_isPrefetch) gate._prefetchSlots.Release();
        }
    }

    /// <param name="Lease">null si no se concedió.</param>
    /// <param name="DeniedReason">Por qué no se concedió (para el log).</param>
    public readonly record struct Entry(Lease? Lease, string? DeniedReason);

    /// <summary>true mientras el cortacircuitos está abierto (YouTube nos bloqueó hace poco).</summary>
    public bool IsBlocked(out TimeSpan remaining)
    {
        lock (_lock)
        {
            remaining = _blockedUntil - DateTime.UtcNow;
            if (remaining > TimeSpan.Zero) return true;
            remaining = TimeSpan.Zero;
            return false;
        }
    }

    /// <summary>
    /// Pide permiso para lanzar una extracción. Nunca lanza: si no se puede ahora,
    /// devuelve Lease = null y el motivo.
    /// </summary>
    public async Task<Entry> TryEnterAsync(bool isPrefetch)
    {
        return isPrefetch ? await TryEnterPrefetchAsync() : await TryEnterPlayAsync();
    }

    // El precalentado es "si sobra": no espera cupo, y solo ocupa parte de los huecos.
    private async Task<Entry> TryEnterPrefetchAsync()
    {
        var denied = PrefetchDeniedReason();
        if (denied != null) return new Entry(null, denied);

        if (!await _prefetchSlots.WaitAsync(_maxPrefetchWait))
            return new Entry(null, "sin hueco libre para precalentar");
        if (!await _slots.WaitAsync(_maxPrefetchWait))
        {
            _prefetchSlots.Release();
            return new Entry(null, "sin hueco libre para precalentar");
        }

        // Pudo cambiar mientras esperaba hueco: se vuelve a comprobar antes de gastar cupo.
        lock (_lock)
        {
            denied = PrefetchDeniedReasonLocked(DateTime.UtcNow);
            if (denied == null) _starts.Enqueue(DateTime.UtcNow);
        }
        if (denied != null)
        {
            _slots.Release();
            _prefetchSlots.Release();
            return new Entry(null, denied);
        }
        return new Entry(new Lease(this, isPrefetch: true), null);
    }

    private string? PrefetchDeniedReason()
    {
        lock (_lock) return PrefetchDeniedReasonLocked(DateTime.UtcNow);
    }

    private string? PrefetchDeniedReasonLocked(DateTime now)
    {
        if (now < _blockedUntil) return BlockedText(_blockedUntil - now);
        if (_probing) return "recuperándose de un bloqueo de YouTube";
        Trim(now);
        return _starts.Count >= _prefetchBudget
            ? $"cupo de precalentado agotado ({_starts.Count} extracciones en el último minuto)"
            : null;
    }

    // Una reproducción real espera su turno (hasta _maxPlayWait) si el minuto está lleno.
    private async Task<Entry> TryEnterPlayAsync()
    {
        var deadline = DateTime.UtcNow + _maxPlayWait;
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (now < _blockedUntil) return new Entry(null, BlockedText(_blockedUntil - now));

                Trim(now);
                // Recién salidos de un bloqueo se tantea con muy poco tráfico.
                var limit = _probing ? Math.Min(2, _maxPerWindow) : _maxPerWindow;
                if (_starts.Count < limit)
                {
                    _starts.Enqueue(now);
                    break;
                }

                // Hueco libre cuando salga de la ventana la extracción que sobra.
                wait = _starts.ElementAt(_starts.Count - limit) + _window - now;
                if (now + wait > deadline)
                    return new Entry(null, $"tope de {limit} extracciones por minuto alcanzado");
            }
            await Task.Delay(wait + TimeSpan.FromMilliseconds(20));
        }

        var left = deadline - DateTime.UtcNow;
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        if (!await _slots.WaitAsync(left))
            return new Entry(null, "servidor ocupado con otras extracciones");
        return new Entry(new Lease(this, isPrefetch: false), null);
    }

    /// <summary>
    /// Cuenta en el cupo una extracción hecha por otra vía desde esta misma IP
    /// (Piped local, YoutubeExplode), para que los respaldos no se salten el tope.
    /// </summary>
    public void RecordExtra()
    {
        lock (_lock) _starts.Enqueue(DateTime.UtcNow);
    }

    /// <summary>
    /// Para peticiones ligeras a YouTube que no son extracciones de audio (p.ej. la
    /// lista de canciones parecidas): gasta una unidad del cupo de precalentado si la
    /// hay, sin esperar. false = ahora no toca (bloqueo, tanteo o cupo agotado).
    /// </summary>
    public bool TryConsume()
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            if (PrefetchDeniedReasonLocked(now) != null) return false;
            _starts.Enqueue(now);
            return true;
        }
    }

    /// <summary>YouTube respondió con un bloqueo: abrir el cortacircuitos.</summary>
    public void ReportBlocked(string detail)
    {
        TimeSpan pause;
        lock (_lock)
        {
            var now = DateTime.UtcNow;
            // Varias extracciones de la misma ráfaga fallan a la vez: cuenta como un solo bloqueo.
            if (now < _blockedUntil) return;
            pause = _backoff[Math.Min(_blockLevel, _backoff.Length - 1)];
            _blockLevel++;
            _blockedUntil = now + pause;
            _probing = true;
        }
        Console.WriteLine(
            $"[Gate] ⛔ YouTube bloqueó la IP ({detail}). Sin extracciones durante {pause.TotalMinutes:F0} min.");
    }

    /// <summary>Una extracción funcionó: YouTube nos atiende con normalidad.</summary>
    public void ReportSuccess()
    {
        bool recovered;
        lock (_lock)
        {
            // Una extracción que ya estaba en marcha puede terminar bien justo
            // después de abrirse el cortacircuitos: no lo cierra antes de tiempo.
            if (DateTime.UtcNow < _blockedUntil) return;
            recovered = _probing;
            _probing = false;
            _blockLevel = 0;
        }
        if (recovered) Console.WriteLine("[Gate] ✅ YouTube vuelve a responder; se reanuda el ritmo normal.");
    }

    private void Trim(DateTime now)
    {
        while (_starts.Count > 0 && now - _starts.Peek() >= _window)
            _starts.Dequeue();
    }

    private static string BlockedText(TimeSpan remaining) =>
        $"YouTube bloqueó la IP; se reintenta en {Math.Max(1, (int)Math.Ceiling(remaining.TotalSeconds))} s";
}
