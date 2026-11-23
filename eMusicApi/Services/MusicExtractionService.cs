using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using eMusicApi.Data;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;

namespace eMusicApi.Services;

/// <summary>
/// Servicio híbrido de extracción de música.
/// Streams: yt-dlp (bajo el control de <see cref="YouTubeGate"/>) y, solo si falla,
/// Piped interno → YoutubeExplode → Piped públicos como respaldo.
/// Caché inteligente: sólo guarda resultados VÁLIDOS (con audioStreams).
/// </summary>
public class MusicExtractionService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly YoutubeClient _youtubeClient;
    // Este servicio es Singleton pero AppDbContext es Scoped: para tocar la base de
    // datos (actualizar favoritos/listas cuando se sustituye un vídeo) hay que abrir
    // un scope propio en cada uso, nunca inyectar AppDbContext directamente aquí.
    private readonly IServiceScopeFactory _scopeFactory;

    // Tope de extracciones por minuto y cortacircuitos ante bloqueos de YouTube.
    // El servicio es Singleton, así que hay un único control para toda la API.
    private readonly YouTubeGate _gate = YouTubeGate.FromEnvironment();

    // Extracciones en marcha, por videoId (ver GetStreamAsync).
    private readonly ConcurrentDictionary<string, PendingStream> _inFlight = new();

    // Instancias Piped en orden de preferencia.
    // NOTA: dentro de Docker, el backend Piped se llama "piped-backend" en la red interna.
    // El dominio externo NO se usa aquí para evitar bucles de red.
    private static readonly string[] PipedInstances = new[]
    {
        "http://piped-backend:8080",           // Red interna Docker (1ª prioridad, la más rápida)
        "https://pipedapi.kavin.rocks",        // Piped público de respaldo
        "https://pipedapi.colby.land",
        "https://piped-api.garudalinux.org",
        "https://api.piped.yt"
    };

    public MusicExtractionService(IHttpClientFactory httpClientFactory, IMemoryCache cache, IServiceScopeFactory scopeFactory)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;
        _scopeFactory = scopeFactory;

        // Crear HttpClient con headers de navegador para YoutubeExplode
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var httpClient = new HttpClient(handler)
        {
            // Sin esto, HttpClient usa 100s por defecto: si YouTube tarda en responder,
            // GetStreamAsync podía quedarse colgado mucho más de lo previsto.
            Timeout = TimeSpan.FromSeconds(15)
        };
        httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
        _youtubeClient = new YoutubeClient(httpClient);
    }

    // Tiempo máximo que se deja vivo un proceso yt-dlp antes de matarlo. YouTube exige
    // resolver un reto JavaScript para firmar las URLs de audio; normalmente tarda
    // segundos, pero a veces se cuelga. Sin este límite, ese único proceso podía
    // bloquear la respuesta de GetStreamAsync indefinidamente.
    private static readonly TimeSpan YtDlpTimeout = TimeSpan.FromSeconds(15);

    // Cuánto tiempo se recuerda que un vídeo falló en todos los extractores, para no
    // repetir la cascada completa (y sus posibles esperas) en cada intento de play.
    private static readonly TimeSpan StreamFailureCooldown = TimeSpan.FromMinutes(10);

    /// <summary>
    /// Ejecuta un proceso externo con un límite de tiempo real. Si se pasa, lo mata
    /// (con todo el árbol de hijos que haya lanzado, p.ej. ffmpeg o deno) en vez de
    /// dejarlo colgado consumiendo CPU/memoria de la Pi sin que nadie lo espere ya.
    ///
    /// Los argumentos se pasan uno a uno (ArgumentList), nunca como una sola cadena:
    /// así un texto que viene de fuera (una búsqueda, un id) llega al proceso como
    /// UN argumento y no puede colar opciones extra a yt-dlp.
    /// </summary>
    private static async Task<(int exitCode, string stdout, string stderr, bool timedOut)> RunWithTimeoutAsync(
        string fileName, IEnumerable<string> arguments, TimeSpan timeout)
    {
        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = fileName,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = new System.Diagnostics.Process { StartInfo = startInfo };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var waitTask = process.WaitForExitAsync();

        if (await Task.WhenAny(waitTask, Task.Delay(timeout)) != waitTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* pudo terminar justo ahora */ }
            return (-1, "", $"yt-dlp excedió {timeout.TotalSeconds:F0}s y fue detenido.", true);
        }

        return (process.ExitCode, await stdoutTask, await stderrTask, false);
    }

    /// <summary>
    /// Acota una tarea de extracción al cts general de GetStreamAsync: si el límite de
    /// tiempo salta, esta rama del Task.WhenAny se resuelve enseguida como fallo (la
    /// tarea real sigue en segundo plano hasta terminar por su cuenta, p.ej. porque
    /// RunWithTimeoutAsync mate el proceso yt-dlp).
    /// </summary>
    private static async Task<(string json, string source)> BoundedBySource(
        Task<(string json, string source)> inner, string source, CancellationToken ct)
    {
        var cancelSignal = new TaskCompletionSource<bool>();
        using var registration = ct.Register(() => cancelSignal.TrySetResult(true));
        var completed = await Task.WhenAny(inner, cancelSignal.Task);
        if (completed == cancelSignal.Task)
            return (BuildErrorJson("Tiempo de extracción agotado."), source);
        return await inner;
    }

    // ─────────────────────────────────────────────────────────────────
    // BÚSQUEDA
    // ─────────────────────────────────────────────────────────────────
    /// <summary>Filtro de Piped del buscador normal: canciones sueltas de YouTube Music.</summary>
    public const string DefaultSearchFilter = "music_songs";

    /// <param name="filter">
    /// Filtro de Piped ("music_songs", "videos", "music_videos"...). Con uno distinto del
    /// normal solo se pregunta a Piped: los respaldos (Invidious, yt-dlp) no entienden
    /// de filtros y devolverían otra cosa.
    /// </param>
    public async Task<string> SearchAsync(string query, string filter = DefaultSearchFilter)
    {
        bool isDefaultFilter = filter == DefaultSearchFilter;
        var cacheKey = isDefaultFilter
            ? $"search_{query.ToLowerInvariant()}"
            : $"search_{filter}_{query.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null)
            return cached;

        foreach (var instance in PipedInstances)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(8);
                var url = $"{instance}/search?q={Uri.EscapeDataString(query)}&filter={Uri.EscapeDataString(filter)}";
                var response = await client.GetAsync(url);

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    // Validar que items exista Y tenga al menos 1 resultado real
                    if (HasNonEmptyItems(json))
                    {
                        _cache.Set(cacheKey, json, TimeSpan.FromMinutes(30));
                        Console.WriteLine($"[Search] OK via {instance}");
                        return json;
                    }
                    Console.WriteLine($"[Search] {instance} devolvió items vacíos, probando siguiente...");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Search] Fallo en {instance}: {ex.Message}");
            }
        }

        if (!isDefaultFilter)
        {
            Console.WriteLine($"[Search] Sin resultados de Piped para '{query}' (filtro {filter}).");
            return "{\"items\":[],\"nextpage\":null}";
        }

        // Sin resultados de ningún proveedor Piped → intentar Invidious (REST API rápida, ~1-2s)
        Console.WriteLine($"[Search] Piped fallaron. Intentando Invidious para '{query}'...");
        var invidiousResult = await SearchWithInvidiousAsync(query);
        if (invidiousResult != null)
        {
            _cache.Set(cacheKey, invidiousResult, TimeSpan.FromMinutes(30));
            return invidiousResult;
        }

        // Último recurso: yt-dlp (lento pero robusto)
        Console.WriteLine($"[Search] Invidious falló. Usando yt-dlp para '{query}'...");
        return await SearchWithYtDlpAsync(query);
    }

    // ─────────────────────────────────────────────────────────────────
    // BÚSQUEDA con Invidious API — Plan B, REST rápida sin JavaScript
    // ─────────────────────────────────────────────────────────────────
    // yt.artemislena.eu e invidious.fdn.fr quitadas el 27/09/2026: totalmente
    // inalcanzables (DNS/red), solo anadian ~12s de espera muerta por peticion.
    private static readonly string[] InvidiousInstances =
    [
        "https://inv.nadeko.net",
        "https://invidious.nerdvpn.de",
        "https://invidious.privacyredirect.com"
    ];

    private async Task<string?> SearchWithInvidiousAsync(string query)
    {
        foreach (var instance in InvidiousInstances)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(6);
                var url = $"{instance}/api/v1/search?q={Uri.EscapeDataString(query)}&type=video&sort_by=relevance";
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                var items = new List<object>();
                foreach (var v in doc.RootElement.EnumerateArray())
                {
                    var type = v.TryGetProperty("type", out var t) ? t.GetString() : "";
                    if (type != "video") continue;

                    var id = v.TryGetProperty("videoId", out var idEl) ? idEl.GetString() ?? "" : "";
                    var title = v.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                    var author = v.TryGetProperty("author", out var authEl) ? authEl.GetString() ?? "" : "";
                    var length = v.TryGetProperty("lengthSeconds", out var lenEl) && lenEl.ValueKind == JsonValueKind.Number
                        ? lenEl.GetInt64() : 0L;
                    var views = v.TryGetProperty("viewCount", out var viewEl) && viewEl.ValueKind == JsonValueKind.Number
                        ? viewEl.GetInt64() : 0L;

                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;

                    // Thumbnail fiable usando la URL estándar de YouTube
                    var thumb = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";

                    items.Add(new
                    {
                        url = $"/watch?v={id}",
                        type = "stream",
                        title = title,
                        thumbnail = thumb,
                        uploaderName = author,
                        uploaderUrl = "",
                        uploaderAvatar = "",
                        uploaderVerified = false,
                        duration = length,
                        views = views,
                        uploaded = 0L,
                        shortDescription = "",
                        isShort = false
                    });
                }

                if (items.Count == 0) continue;

                var result = BuildSearchJson(items);
                Console.WriteLine($"[Invidious Search] ✅ {items.Count} resultados via {instance}");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Invidious Search] Fallo en {instance}: {ex.Message}");
            }
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────
    // TRENDING
    // ─────────────────────────────────────────────────────────────────
    public async Task<string> GetTrendingAsync()
    {
        const string cacheKey = "trending_music";
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null)
            return cached;

        foreach (var instance in PipedInstances)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(8);
                var response = await client.GetAsync($"{instance}/trending?region=US");

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    if (json.StartsWith("[") || json.Contains("\"items\""))
                    {
                        _cache.Set(cacheKey, json, TimeSpan.FromHours(1));
                        Console.WriteLine($"[Trending] OK via {instance}");
                        return json;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Trending] Fallo en {instance}: {ex.Message}");
            }
        }

        // Fallback: Invidious trending music
        Console.WriteLine("[Trending] Piped fallaron. Intentando Invidious...");
        var invidiousResult = await GetTrendingWithInvidiousAsync();
        if (invidiousResult != null)
        {
            _cache.Set(cacheKey, invidiousResult, TimeSpan.FromHours(1));
            return invidiousResult;
        }

        return "[]";
    }

    private async Task<string?> GetTrendingWithInvidiousAsync()
    {
        foreach (var instance in InvidiousInstances)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(6);
                var response = await client.GetAsync($"{instance}/api/v1/trending?type=music&region=US");
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Array) continue;

                var items = new List<object>();
                foreach (var v in doc.RootElement.EnumerateArray())
                {
                    var id     = v.TryGetProperty("videoId", out var idEl)    ? idEl.GetString()    ?? "" : "";
                    var title  = v.TryGetProperty("title",   out var titleEl) ? titleEl.GetString() ?? "" : "";
                    var author = v.TryGetProperty("author",  out var authEl)  ? authEl.GetString()  ?? "" : "";
                    var length = v.TryGetProperty("lengthSeconds", out var lenEl) && lenEl.ValueKind == JsonValueKind.Number
                        ? lenEl.GetInt64() : 0L;

                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;

                    items.Add(new
                    {
                        url              = $"/watch?v={id}",
                        type             = "stream",
                        title,
                        thumbnail        = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg",
                        uploaderName     = author,
                        uploaderUrl      = "",
                        uploaderAvatar   = "",
                        uploaderVerified = false,
                        duration         = length,
                        views            = 0L,
                        uploaded         = 0L,
                        shortDescription = "",
                        isShort          = false
                    });
                }

                if (items.Count == 0) continue;

                var result = BuildSearchJson(items);
                Console.WriteLine($"[Invidious Trending] ✅ {items.Count} tendencias via {instance}");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Invidious Trending] Fallo en {instance}: {ex.Message}");
            }
        }
        return null;
    }

    // ─────────────────────────────────────────────────────────────────
    // BÚSQUEDA con yt-dlp — Plan B cuando todos los Piped fallan
    // ─────────────────────────────────────────────────────────────────
    private async Task<string> SearchWithYtDlpAsync(string query)
    {
        // Con YouTube bloqueándonos, buscar con yt-dlp solo alargaría el bloqueo.
        if (_gate.IsBlocked(out _))
        {
            Console.WriteLine($"[yt-dlp Search] Omitida para '{query}': YouTube está limitando la IP.");
            return "{\"items\":[],\"nextpage\":null}";
        }

        try
        {
            // Primero intentamos YouTube Music (resultados de canciones precisos)
            var ytMusicResult = await RunYtDlpSearch(
                $"https://music.youtube.com/search?q={Uri.EscapeDataString(query)}&sp=EgWKAQIIAWoKEAMQBBAJEAoQBQ%3D%3D",
                query, isMusic: true);

            if (ytMusicResult.Count > 0)
            {
                Console.WriteLine($"[yt-dlp Search] ✅ {ytMusicResult.Count} resultados de YouTube Music para '{query}'");
                return BuildSearchJson(ytMusicResult);
            }

            // Fallback: YouTube general — resultado más limitado
            var ytResult = await RunYtDlpSearch($"ytsearch10:{query}", query, isMusic: false);
            Console.WriteLine($"[yt-dlp Search] ✅ {ytResult.Count} resultados de YouTube para '{query}'");
            return BuildSearchJson(ytResult);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[yt-dlp Search] ERROR: {ex.Message}");
            return "{\"items\":[],\"nextpage\":null}";
        }
    }

    private async Task<List<object>> RunYtDlpSearch(string searchArg, string query, bool isMusic)
    {
        // "--" marca el fin de las opciones: lo que va detrás (que contiene el texto
        // que escribió el usuario) solo puede ser la búsqueda, nunca una opción.
        var (_, output, _, timedOut) = await RunWithTimeoutAsync(
            "yt-dlp",
            new[] { "--force-ipv4", "--flat-playlist", "-j", "--quiet", "--no-warnings", "--playlist-end", "20", "--", searchArg },
            YtDlpTimeout);
        if (timedOut)
            Console.WriteLine($"[yt-dlp Search] ⏱ Timeout buscando '{query}'; proceso detenido.");

        var items = new List<object>();
        if (string.IsNullOrWhiteSpace(output)) return items;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                var title = root.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                var uploader = root.TryGetProperty("uploader", out var uploaderEl) ? uploaderEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(uploader))
                    uploader = root.TryGetProperty("channel", out var channelEl) ? channelEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(uploader))
                    uploader = root.TryGetProperty("artist", out var artistEl) ? artistEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(uploader))
                    uploader = root.TryGetProperty("creator", out var creatorEl) ? creatorEl.GetString() ?? "" : "";
                // Thumbnail fiable con URL estándar de YouTube (siempre funciona)
                var thumb = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";
                var duration = root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number
                    ? (long)durEl.GetDouble() : 0L;
                var views = root.TryGetProperty("view_count", out var viewEl) && viewEl.ValueKind == JsonValueKind.Number
                    ? viewEl.GetInt64() : 0L;

                if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;
                // Filtrar clips cortos de gaming (<30s) en búsquedas generales
                if (!isMusic && duration > 0 && duration < 30) continue;

                items.Add(new
                {
                    url = $"/watch?v={id}",
                    type = "stream",
                    title = title,
                    thumbnail = thumb,
                    uploaderName = uploader,
                    uploaderUrl = "",
                    uploaderAvatar = "",
                    uploaderVerified = false,
                    duration = duration,
                    views = views,
                    uploaded = 0L,
                    shortDescription = "",
                    isShort = false
                });
            }
            catch { /* ignorar líneas malformadas */ }
        }
        return items;
    }

    private static string BuildSearchJson(List<object> items)
    {
        return JsonSerializer.Serialize(
            new { items, nextpage = (string?)null, suggestion = (string?)null, corrected = false },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    // ─────────────────────────────────────────────────────────────────
    // STREAMS — El más crítico: extractores en PARALELO, gana el primero
    // ─────────────────────────────────────────────────────────────────
    public Task<string> GetStreamAsync(string videoId) => GetStreamAsync(videoId, allowSubstitute: true);

    /// <summary>
    /// Duracion real (en milisegundos) de un video, SOLO si ya esta en cache por
    /// haberse reproducido hace poco (no dispara ninguna llamada de red nueva).
    /// Lo usa HistoryController para corregir el caso en que el cliente registra
    /// una reproduccion con Duration=0 (p.ej. si registra antes de conocer la
    /// duracion real del track).
    /// </summary>
    public long? TryGetCachedDurationMs(string videoId)
    {
        if (!_cache.TryGetValue($"stream_{videoId}", out string? cached) || string.IsNullOrEmpty(cached))
            return null;
        try
        {
            using var doc = JsonDocument.Parse(cached);
            if (doc.RootElement.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
            {
                var seconds = durEl.GetDouble();
                if (seconds > 0) return (long)(seconds * 1000);
            }
        }
        catch { /* JSON de error cacheado, no un stream valido: se ignora */ }
        return null;
    }

    /// <param name="allowSubstitute">
    /// Si es true y el vídeo pedido resulta estar caído en todas partes (borrado,
    /// privado, bloqueado), se intenta servir la MISMA canción desde otra fuente en
    /// vez de devolver un error: la app nunca se entera, sigue sonando con
    /// normalidad. Se pone en false en la llamada recursiva de esa búsqueda para no
    /// encadenar sustituciones indefinidamente.
    /// </param>
    private async Task<string> GetStreamAsync(string videoId, bool allowSubstitute, bool isPrefetch = false)
    {
        // El id acaba en la línea de órdenes de yt-dlp y en URLs de otros servicios:
        // solo se acepta lo que tiene forma de id de YouTube.
        if (!IsValidVideoId(videoId))
            return BuildErrorJson("Identificador de vídeo no válido.");

        var cacheKey = $"stream_{videoId}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null)
            return cached;

        // Si este vídeo falló en todos los extractores hace poco (borrado, privado,
        // bloqueado...), no repetir la cascada completa en cada intento de play:
        // se responde el fallo al instante y se reintenta solo pasado el enfriamiento.
        var failKey = $"stream_failed_{videoId}";
        if (_cache.TryGetValue(failKey, out string? recentFailure) && recentFailure != null)
        {
            Console.WriteLine($"[Stream] {videoId} falló recientemente; devolviendo error sin reintentar todavía.");
            return recentFailure;
        }

        // Una sola extracción en marcha por vídeo: si llegan varias peticiones de la
        // misma canción a la vez (play + precalentado, reintentos de la app...), todas
        // esperan el mismo resultado en vez de lanzar cada una su yt-dlp contra YouTube.
        var mine = new PendingStream(
            new Lazy<Task<string>>(() => ResolveStreamAsync(videoId, allowSubstitute, isPrefetch)),
            isPrefetch);
        var pending = _inFlight.GetOrAdd(videoId, mine);
        string result;
        try
        {
            result = await pending.Task.Value;
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<string, PendingStream>(videoId, pending));
        }

        // Esta reproducción se sumó a un precalentado que no dio resultado (p.ej. se
        // descartó por falta de cupo): un play real no se conforma, lo intenta él.
        if (!isPrefetch && pending.IsPrefetch && !HasValidAudioStreams(result))
            return await GetStreamAsync(videoId, allowSubstitute, isPrefetch: false);

        return result;
    }

    /// <summary>
    /// Precalentado pedido por la app (resultados de búsqueda, siguientes de la cola).
    /// Es "si sobra cupo": no encadena más precalentados ni busca sustitutos.
    /// </summary>
    public void Prefetch(string videoId) =>
        _ = GetStreamAsync(videoId, allowSubstitute: false, isPrefetch: true);

    private sealed record PendingStream(Lazy<Task<string>> Task, bool IsPrefetch);

    private static bool IsValidVideoId(string? id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 32 &&
        id.All(c => char.IsAsciiLetterOrDigit(c) || c == '-' || c == '_');

    /// <summary>
    /// Extracción real de un vídeo que no está en caché. Orden:
    ///  1. yt-dlp, pasando por <see cref="YouTubeGate"/> (tope por minuto y cortacircuitos).
    ///     Es el único extractor que funciona hoy: entre el 19/10 y el 23/11/2026 ganó
    ///     las 1.326 extracciones; Piped local y YoutubeExplode, ninguna.
    ///  2. Solo si yt-dlp falla: Piped y YoutubeExplode como respaldo. Antes se lanzaban
    ///     siempre en paralelo con yt-dlp y multiplicaban las peticiones a YouTube desde
    ///     la IP de la Pi sin aportar nada.
    /// </summary>
    private async Task<string> ResolveStreamAsync(string videoId, bool allowSubstitute, bool isPrefetch)
    {
        var cacheKey = $"stream_{videoId}";
        try
        {
            // true = ahora mismo no se puede (o no se debe) llamar a YouTube desde esta IP.
            var youTubeUnavailable = false;

            var entry = await _gate.TryEnterAsync(isPrefetch);
            if (entry.Lease == null)
            {
                youTubeUnavailable = true;
                Console.WriteLine(isPrefetch
                    ? $"[Stream] Precalentado de {videoId} descartado: {entry.DeniedReason}."
                    : $"[Stream] {videoId}: no se llama a YouTube ahora ({entry.DeniedReason}).");
            }
            else
            {
                string ytJson;
                bool blocked;
                using (entry.Lease)
                    (ytJson, blocked) = await ExtractWithYtDlpAsync(videoId);

                if (HasValidAudioStreams(ytJson))
                {
                    _gate.ReportSuccess();
                    return OnStreamResolved(videoId, ytJson, "yt-dlp", isPrefetch);
                }

                if (blocked)
                {
                    _gate.ReportBlocked($"al extraer {videoId}");
                    youTubeUnavailable = true;
                }
                else
                {
                    Console.WriteLine($"[Stream] yt-dlp respondió sin audioStreams válidos para {videoId}.");
                }
            }

            // Un precalentado termina aquí: ni respaldos ni marcar la canción como
            // fallida. Si luego se reproduce de verdad, se intentará todo entonces.
            if (isPrefetch)
                return BuildErrorJson("Precalentado sin resultado.");

            // Respaldos. Si YouTube nos está limitando, solo los Piped públicos (salen
            // desde otra IP): Piped local y YoutubeExplode usan la misma IP bloqueada y
            // seguir llamando desde ella es lo que alarga el bloqueo.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var tasks = new List<Task<(string json, string source)>>();
            if (!youTubeUnavailable)
            {
                _gate.RecordExtra();
                tasks.Add(TryPipedInstanceAsync(PipedInstances[0], videoId, cts.Token));
                tasks.Add(BoundedBySource(Task.Run(async () =>
                {
                    var json = await ExtractWithYoutubeExplodeAsync(videoId, cacheKey: null);
                    return (json, "YoutubeExplode");
                }), "YoutubeExplode", cts.Token));
            }
            for (var i = 1; i <= 2 && i < PipedInstances.Length; i++)
                tasks.Add(TryPipedInstanceAsync(PipedInstances[i], videoId, cts.Token));

            while (tasks.Count > 0)
            {
                var completed = await Task.WhenAny(tasks);
                tasks.Remove(completed);

                try
                {
                    var (json, source) = await completed;
                    if (HasValidAudioStreams(json))
                    {
                        cts.Cancel(); // Cancelar el resto
                        return OnStreamResolved(videoId, json, source, isPrefetch);
                    }
                    Console.WriteLine($"[Stream] {source} respondió sin audioStreams válidos.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Stream] Extractor falló: {ex.Message}");
                }
            }

            // El problema es el acceso a YouTube, no esta canción: NO se marca como
            // fallida (antes cada canción quedaba 10 min inservible aunque el bloqueo ya
            // hubiera pasado) y no se busca sustituto. La app reintentará.
            if (youTubeUnavailable)
            {
                var message = _gate.IsBlocked(out var remaining)
                    ? $"YouTube está limitando temporalmente al servidor. Reintenta en {Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes))} min."
                    : "Demasiadas canciones seguidas. Reintenta en unos segundos.";
                Console.WriteLine($"[Stream] {videoId}: sin audio por ahora, no se marca como fallida ({message})");
                return BuildErrorJson(message);
            }

            // Nada pudo extraer audio de este vídeo. Antes de rendirnos, intentar servir
            // la MISMA canción desde otra fuente para que el reproductor no note nada.
            if (allowSubstitute)
            {
                var substitute = await TryFindSubstituteAsync(videoId);
                if (substitute != null)
                {
                    _cache.Set(cacheKey, substitute, TimeSpan.FromMinutes(55));
                    Console.WriteLine($"[Stream] {videoId}: servido con una fuente alternativa de la misma canción.");
                    return substitute;
                }
            }

            var failureJson = BuildErrorJson("Todos los extractores fallaron.");
            _cache.Set($"stream_failed_{videoId}", failureJson, StreamFailureCooldown);
            Console.WriteLine($"[Stream] {videoId}: todos los extractores fallaron (sin sustituto disponible); en enfriamiento {StreamFailureCooldown.TotalMinutes:F0} min.");
            return failureJson;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Stream] Error inesperado resolviendo {videoId}: {ex.Message}");
            return BuildErrorJson(ex.Message);
        }
    }

    /// <summary>
    /// Guarda en caché un stream recién extraído y, en segundo plano, le añade
    /// canciones relacionadas y precalienta las 2 más probables de sonar después.
    /// </summary>
    private string OnStreamResolved(string videoId, string json, string source, bool isPrefetch)
    {
        var cacheKey = $"stream_{videoId}";
        _cache.Set(cacheKey, json, TimeSpan.FromMinutes(55));
        Console.WriteLine($"[Stream] OK via {source}{(isPrefetch ? " (precalentado)" : "")} para {videoId}");

        // Enriquecer related streams en background (no bloquea el play) y
        // precalentar (prefetch) las 2 canciones mas probables de sonar despues.
        // Arregla el caso "una cancion tarda 4-5s en resolver y el reproductor la
        // salta por tardar": si ya llego precalentada desde que sonaba la anterior,
        // el play es instantaneo sin importar cuanto tarde esa cancion en concreto.
        _ = Task.Run(async () =>
        {
            try
            {
                var enriched = await EnrichRelatedStreamsIfEmptyAsync(json, videoId);
                if (enriched != json)
                    _cache.Set(cacheKey, enriched, TimeSpan.FromMinutes(55));

                // Solo desde una reproduccion real: si esta llamada YA es un
                // precalentado, no encadenar mas (evita una cascada sin limite que
                // multiplica las peticiones a YouTube y dispara su bloqueo por
                // "demasiadas peticiones" — nos paso el 29/09, ver commit).
                if (isPrefetch) return;

                foreach (var relatedId in ExtractRelatedVideoIds(enriched, max: 2))
                {
                    if (relatedId == videoId) continue;
                    if (_cache.TryGetValue($"stream_{relatedId}", out string? _)) continue; // ya calentada
                    Prefetch(relatedId); // fire-and-forget, un solo nivel
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Stream] Error enriqueciendo {videoId}: {ex.Message}");
            }
        });

        return json;
    }

    // ─────────────────────────────────────────────────────────────────
    // SUSTITUCIÓN — cuando un vídeo está caído en todas partes, buscar la
    // misma canción en otra fuente para que la reproducción no se note.
    // ─────────────────────────────────────────────────────────────────
    private async Task<string?> TryFindSubstituteAsync(string deadVideoId)
    {
        var (title, author) = await GetTitleAuthorForSubstituteAsync(deadVideoId);
        if (string.IsNullOrWhiteSpace(title))
        {
            Console.WriteLine($"[Substitute] Sin metadatos para {deadVideoId}; imposible buscar sustituto.");
            return null;
        }

        var query = string.IsNullOrWhiteSpace(author) ? title : $"{author} {title}";
        var searchJson = await SearchWithInvidiousAsync(query);
        var candidateId = searchJson != null ? FirstVideoIdFrom(searchJson, excluding: deadVideoId) : null;
        if (candidateId == null)
        {
            Console.WriteLine($"[Substitute] Sin resultados de búsqueda para '{query}' (original: {deadVideoId}).");
            return null;
        }

        Console.WriteLine($"[Substitute] {deadVideoId} ('{title}') → probando {candidateId}");
        var candidateStream = await GetStreamAsync(candidateId, allowSubstitute: false);
        if (!HasValidAudioStreams(candidateStream)) return null;

        // El sustituto funciona: actualizar el id guardado en favoritos/listas para
        // que apunten directo a él. Así no se pierde el "me gusta" ni la posición
        // en la lista (se cambia el id en el sitio, todo lo demás queda igual), y
        // las próximas reproducciones no tienen que volver a buscar sustituto.
        await RepointSavedReferencesAsync(deadVideoId, candidateId);

        return candidateStream;
    }

    /// <summary>
    /// Cuando un vídeo muerto se sustituye por otro que sí funciona, cambia el
    /// videoId guardado en Favoritos y en las canciones de las Listas para que
    /// apunten directo al sustituto. No se borra ni se vuelve a crear nada: se
    /// actualiza el id en el sitio, así que el "me gusta", la fecha, la miniatura
    /// y la posición en cada lista se conservan tal cual.
    /// </summary>
    private async Task RepointSavedReferencesAsync(string oldVideoId, string newVideoId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            var oldFavorite = await db.Favorites.FirstOrDefaultAsync(f => f.Id == oldVideoId);
            if (oldFavorite != null)
            {
                // Id es la clave primaria: si el sustituto ya estaba guardado como
                // favorito por su cuenta, no se puede duplicar la clave, así que se
                // quita la referencia vieja en vez de actualizarla.
                if (await db.Favorites.AnyAsync(f => f.Id == newVideoId))
                    db.Favorites.Remove(oldFavorite);
                else
                    await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE Favorites SET Id = {newVideoId} WHERE Id = {oldVideoId}");
            }

            var playlists = await db.Playlists.Where(p => p.SongsJson.Contains(oldVideoId)).ToListAsync();
            foreach (var playlist in playlists)
            {
                var array = JsonNode.Parse(playlist.SongsJson ?? "[]")?.AsArray();
                if (array == null) continue;
                foreach (var song in array)
                {
                    if (song?["videoId"]?.GetValue<string>() == oldVideoId)
                        song["videoId"] = newVideoId;
                }
                playlist.SongsJson = array.ToJsonString();
            }

            if (oldFavorite != null || playlists.Count > 0)
            {
                await db.SaveChangesAsync();
                Console.WriteLine(
                    $"[Substitute] Referencias actualizadas {oldVideoId} → {newVideoId}: " +
                    $"{(oldFavorite != null ? "favorito, " : "")}{playlists.Count} lista(s).");
            }
        }
        catch (Exception ex)
        {
            // No debe tumbar la reproducción: el sustituto ya se está sirviendo,
            // esto es solo "curar" la referencia guardada para la próxima vez.
            Console.WriteLine($"[Substitute] No se pudieron actualizar referencias guardadas: {ex.Message}");
        }
    }

    /// <summary>Título y autor de un vídeo vía Invidious, aunque su audio ya no se pueda extraer.</summary>
    private async Task<(string title, string author)> GetTitleAuthorForSubstituteAsync(string videoId)
    {
        foreach (var instance in InvidiousInstances.Take(3))
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(5);
                var url = $"{instance}/api/v1/videos/{videoId}?fields=title,author";
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var title = doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var author = doc.RootElement.TryGetProperty("author", out var a) ? a.GetString() ?? "" : "";
                if (!string.IsNullOrWhiteSpace(title))
                    return (title, author);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Substitute] Fallo obteniendo metadatos de {videoId} en {instance}: {ex.Message}");
            }
        }
        return ("", "");
    }

    /// <summary>Hasta <paramref name="max"/> videoIds de relatedStreams (JSON de /streams enriquecido), en orden.</summary>
    private static List<string> ExtractRelatedVideoIds(string enrichedStreamJson, int max)
    {
        var ids = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(enrichedStreamJson);
            if (!doc.RootElement.TryGetProperty("relatedStreams", out var related)) return ids;
            foreach (var item in related.EnumerateArray())
            {
                if (ids.Count >= max) break;
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var idx = url.IndexOf("?v=", StringComparison.Ordinal);
                var id = idx >= 0 ? url[(idx + 3)..] : "";
                if (!string.IsNullOrEmpty(id) && !ids.Contains(id)) ids.Add(id);
            }
        }
        catch { /* JSON inesperado: sin candidatos que precalentar */ }
        return ids;
    }

    /// <summary>Primer videoId de un JSON de búsqueda {"items":[...]}, distinto de <paramref name="excluding"/>.</summary>
    private static string? FirstVideoIdFrom(string searchJson, string excluding)
    {
        try
        {
            using var doc = JsonDocument.Parse(searchJson);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return null;
            foreach (var item in items.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                var idx = url.IndexOf("?v=", StringComparison.Ordinal);
                var id = idx >= 0 ? url[(idx + 3)..] : "";
                if (!string.IsNullOrEmpty(id) && id != excluding) return id;
            }
        }
        catch { /* JSON inesperado: sin candidato */ }
        return null;
    }

    private async Task<(string json, string source)> TryPipedInstanceAsync(
        string instance, string videoId, CancellationToken ct)
    {
        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var response = await client.GetAsync(
                $"{instance}/streams/{Uri.EscapeDataString(videoId)}", ct);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(ct);
                return (json, $"Piped:{instance}");
            }
            return (BuildErrorJson($"HTTP {(int)response.StatusCode}"), $"Piped:{instance}");
        }
        catch (Exception ex)
        {
            return (BuildErrorJson(ex.Message), $"Piped:{instance}");
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // EXTRACTOR NATIVO con YoutubeExplode
    // ─────────────────────────────────────────────────────────────────
    private async Task<string> ExtractWithYoutubeExplodeAsync(string videoId, string? cacheKey)
    {
        try
        {
            var video = await _youtubeClient.Videos.GetAsync(videoId);
            var manifest = await _youtubeClient.Videos.Streams.GetManifestAsync(videoId);

            // Preferir el audio de mayor calidad (webm/opus)
            var audioStream = manifest.GetAudioOnlyStreams()
                .OrderByDescending(s => s.Bitrate)
                .FirstOrDefault();

            if (audioStream == null)
            {
                Console.WriteLine($"[YoutubeExplode] No se encontró stream de audio para {videoId}");
                return BuildErrorJson("No se pudo obtener el stream de audio.");
            }

            // Además, el mejor mp4/AAC: iOS no puede decodificar Opus/WebM. Si el elegido
            // arriba ya es mp4 no hace falta duplicarlo.
            var m4aStream = audioStream.Container.Name.Equals("mp4", StringComparison.OrdinalIgnoreCase)
                ? null
                : manifest.GetAudioOnlyStreams()
                    .Where(s => s.Container.Name.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.Bitrate)
                    .FirstOrDefault();

            if (m4aStream == null && !audioStream.Container.Name.Equals("mp4", StringComparison.OrdinalIgnoreCase))
                Console.WriteLine($"[YoutubeExplode] ⚠ Sin mp4/AAC para {videoId}: iOS no podrá reproducirlo.");

            // Construir JSON compatible con el formato Piped que espera la app MAUI
            var resultJson = BuildCompatibleJson(video, audioStream, m4aStream);
            if (cacheKey != null)
            {
                resultJson = await EnrichRelatedStreamsIfEmptyAsync(resultJson, videoId);
                _cache.Set(cacheKey, resultJson, TimeSpan.FromMinutes(55));
            }
            Console.WriteLine($"[YoutubeExplode] ✅ Stream extraído nativamente para: {video.Title}");
            return resultJson;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YoutubeExplode] ERROR: {ex.Message}");
            return BuildErrorJson(ex.Message); // Se intentará yt-dlp después
        }
    }

    /// <summary>
    /// true si la salida de error de yt-dlp indica que YouTube está limitando o
    /// bloqueando la IP de la Pi (un problema de TODAS las canciones, no de este vídeo).
    /// </summary>
    internal static bool LooksLikeYouTubeBlock(string? stderr)
    {
        if (string.IsNullOrEmpty(stderr)) return false;
        return stderr.Contains("HTTP Error 429", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("Too Many Requests", StringComparison.OrdinalIgnoreCase)
            || stderr.Contains("not a bot", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Lo relevante de la salida de error de yt-dlp, acotado para el log.</summary>
    private static string SummarizeYtDlpError(string stderr)
    {
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => l.StartsWith("ERROR", StringComparison.Ordinal) || l.StartsWith("WARNING: [youtube]", StringComparison.Ordinal))
            .Select(l => l.Length > 220 ? l[..220] + "…" : l)
            .Take(4)
            .ToList();
        if (lines.Count > 0) return string.Join(" | ", lines);
        var trimmed = stderr.Trim();
        return trimmed.Length > 300 ? trimmed[..300] + "…" : trimmed;
    }

    private static string JsonString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";

    // ─────────────────────────────────────────────────────────────────
    // EXTRACTOR yt-dlp — el único que funciona hoy contra YouTube
    // ─────────────────────────────────────────────────────────────────

    /// <summary>Lo que una ejecución de yt-dlp saca de un vídeo.</summary>
    private sealed record YtDlpAudio(
        string AudioUrl, string? M4aUrl, string Title, string Thumbnail, string Uploader, long DurationSecs,
        DateTimeOffset? AvailableAt, bool BlockedByYouTube, string Error)
    {
        public bool HasAudio => AudioUrl.Length > 0;

        /// <summary>Direcciones de Google tal cual, sin pasar por nuestro proxy.</summary>
        public string[] DirectUrls =>
            string.IsNullOrWhiteSpace(M4aUrl) || M4aUrl == AudioUrl ? new[] { AudioUrl } : new[] { AudioUrl, M4aUrl! };
    }

    // "Clientes" de YouTube con los que yt-dlp pide el audio, en orden de preferencia.
    //
    //  1. visionos: una sola petición a YouTube, sin reto JavaScript y, sobre todo, sin
    //     espera por anuncio: la dirección sirve al instante. Medido el 23/11/2026 en 10
    //     vídeos (incluido un mix de 73 min): 1,7-2,4 s por extracción, los dos formatos
    //     (Opus y m4a) y audio descargable al momento en los 10.
    //  2. null = los clientes por defecto de yt-dlp (hoy "tv samsung"): ~4 s, y si el
    //     vídeo lleva anuncio YouTube no sirve el audio hasta pasados ~5 s (ver
    //     WaitUntilPlayableAsync). Queda de respaldo por si visionos deja de funcionar o
    //     no trae formatos para algún vídeo.
    private static readonly string?[] YtDlpPlayerClients = { "visionos", null };

    /// <returns>
    /// El JSON del stream (o un JSON de error) y si el fallo fue un bloqueo de YouTube a
    /// la IP, para que el llamador abra el cortacircuitos.
    /// </returns>
    private async Task<(string json, bool blockedByYouTube)> ExtractWithYtDlpAsync(string videoId)
    {
        try
        {
            string? lastJson = null;
            var lastError = "sin formatos de audio";

            foreach (var client in YtDlpPlayerClients)
            {
                var clientName = client ?? "clientes por defecto";
                var audio = await RunYtDlpAudioAsync(videoId, client);

                if (!audio.HasAudio)
                {
                    Console.WriteLine($"[yt-dlp] ERROR ({clientName}): {audio.Error}");
                    // Un bloqueo de la IP afecta a todos los clientes: insistir con
                    // otro solo alargaría el bloqueo.
                    if (audio.BlockedByYouTube)
                        return (BuildErrorJson($"yt-dlp falló: {audio.Error}"), true);
                    lastError = audio.Error;
                    continue;
                }

                var json = BuildYtDlpStreamJson(videoId, audio);
                if (await WaitUntilPlayableAsync(videoId, audio.DirectUrls, audio.AvailableAt))
                {
                    Console.WriteLine($"[yt-dlp] ✅ Audio extraído exitosamente: {audio.Title}");
                    return (json, false);
                }

                // De vez en cuando YouTube entrega una dirección que no llega a
                // funcionar nunca (visto ~1 de cada 10 con los clientes por defecto).
                Console.WriteLine($"[yt-dlp] ⚠ {videoId}: la dirección obtenida con {clientName} responde 403; se prueba otra vía.");
                lastJson = json;
            }

            // Ninguna vía dio una dirección comprobada. Si hay alguna sin comprobar se
            // entrega igualmente: puede que funcione desde el reproductor.
            return lastJson != null
                ? (lastJson, false)
                : (BuildErrorJson($"yt-dlp falló: {lastError}"), false);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[yt-dlp] EXCEPCIÓN: {ex.Message}");
            return (BuildErrorJson($"yt-dlp falló: {ex.Message}"), false);
        }
    }

    /// <summary>
    /// UNA ejecución de yt-dlp para un vídeo. Antes eran tres por canción (bestaudio,
    /// m4a y metadatos), cada una con su propia tanda de peticiones a YouTube.
    ///
    /// "bestaudio,bestaudio[ext=m4a]" selecciona dos formatos y --print escribe una
    /// línea JSON por cada uno, en ese orden:
    ///   1ª: el bestaudio de siempre (Opus/WebM), con los metadatos del vídeo.
    ///   2ª: el mejor m4a/AAC, solo para iOS (AVFoundation no decodifica Opus).
    ///       Si el vídeo no tiene m4a, simplemente no sale esa línea.
    /// </summary>
    /// <param name="playerClient">Cliente de YouTube a usar; null = los de yt-dlp por defecto.</param>
    private async Task<YtDlpAudio> RunYtDlpAudioAsync(string videoId, string? playerClient)
    {
        var arguments = new List<string> { "--force-ipv4", "--no-playlist", "--quiet" };
        if (playerClient != null)
        {
            arguments.Add("--extractor-args");
            arguments.Add($"youtube:player_client={playerClient}");
        }
        arguments.AddRange(new[]
        {
            "--format", "bestaudio,bestaudio[ext=m4a]",
            "--print", "%(.{url,ext,title,duration,thumbnail,uploader,available_at})j",
            $"https://www.youtube.com/watch?v={videoId}"
        });

        // Con límite de tiempo real: si yt-dlp se queda esperando (p.ej. resolviendo un
        // reto JS), se mata en vez de dejar la petición colgada.
        var (_, stdout, errorOut, timedOut) = await RunWithTimeoutAsync("yt-dlp", arguments, YtDlpTimeout);
        if (timedOut)
            Console.WriteLine($"[yt-dlp] ⏱ Timeout obteniendo audio de {videoId}; proceso detenido.");

        var audioUrl = "";
        string? m4aUrl = null;
        var title = videoId;
        var thumbnail = "";
        var uploader = "";
        var durationSecs = 0L;
        DateTimeOffset? availableAt = null;

        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var url = JsonString(root, "url");
                if (string.IsNullOrWhiteSpace(url)) continue;

                // Cada formato trae su propia hora de disponibilidad: cuenta la más tardía.
                if (root.TryGetProperty("available_at", out var avEl) && avEl.ValueKind == JsonValueKind.Number)
                {
                    var at = DateTimeOffset.FromUnixTimeSeconds((long)Math.Ceiling(avEl.GetDouble()));
                    if (availableAt == null || at > availableAt) availableAt = at;
                }

                if (audioUrl.Length == 0)
                {
                    audioUrl = url;
                    var printedTitle = JsonString(root, "title");
                    if (printedTitle.Length > 0) title = printedTitle;
                    thumbnail = JsonString(root, "thumbnail");
                    // El canal/artista es imprescindible para buscar canciones
                    // relacionadas despues (EnrichRelatedStreamsIfEmptyAsync se niega
                    // a buscar sin artista, para no traer resultados aleatorios).
                    uploader = JsonString(root, "uploader");
                    if (uploader == "NA") uploader = "";
                    if (root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number)
                        durationSecs = (long)durEl.GetDouble();
                }

                // Igual que antes: si el propio bestaudio ya es m4a, tambien se
                // publica como entrada m4a (iOS busca la de mimeType audio/mp4).
                if (m4aUrl == null && JsonString(root, "ext") == "m4a")
                    m4aUrl = url;
            }
            catch { /* línea que no es JSON: se ignora */ }
        }

        bool blocked = audioUrl.Length == 0 && !timedOut && LooksLikeYouTubeBlock(errorOut);
        var error = audioUrl.Length == 0 ? SummarizeYtDlpError(errorOut) : "";
        return new YtDlpAudio(audioUrl, m4aUrl, title, thumbnail, uploader, durationSecs, availableAt, blocked, error);
    }

    /// <summary>JSON con el formato Piped que espera la app, a partir de lo que dio yt-dlp.</summary>
    private static string BuildYtDlpStreamJson(string videoId, YtDlpAudio audio)
    {
        var audioUrl = audio.AudioUrl;
        var m4aUrl = audio.M4aUrl;
        var title = audio.Title;
        var thumbnail = audio.Thumbnail;
        var uploader = audio.Uploader;
        var durationSecs = audio.DurationSecs;

        // El WebM/Opus va SIEMPRE PRIMERO y con el mismo bitrate declarado que antes.
        // La app Android elige con maxByOrNull { bitrate }, que ante empate se queda con
        // el primer elemento: su comportamiento no cambia en absoluto y el APK ya
        // distribuido sigue funcionando igual. El m4a se agrega solo para iOS, que no
        // puede decodificar Opus.
        var audioStreamsArray = string.IsNullOrWhiteSpace(m4aUrl)
            ? new[]
            {
                new
                {
                    url = RewriteToOwnProxy(audioUrl),
                    format = "webm",
                    quality = "bestaudio",
                    mimeType = "audio/webm",
                    bitrate = 128000L,
                    videoOnly = false,
                    itag = 0,
                    codec = "opus"
                }
            }
            : new[]
            {
                new
                {
                    url = RewriteToOwnProxy(audioUrl),
                    format = "webm",
                    quality = "bestaudio",
                    mimeType = "audio/webm",
                    bitrate = 128000L,
                    videoOnly = false,
                    itag = 0,
                    codec = "opus"
                },
                new
                {
                    url = RewriteToOwnProxy(m4aUrl!),
                    format = "m4a",
                    quality = "bestaudio",
                    mimeType = "audio/mp4",
                    bitrate = 128000L,
                    videoOnly = false,
                    itag = 0,
                    codec = "aac"
                }
            };

        if (string.IsNullOrWhiteSpace(m4aUrl))
            Console.WriteLine($"[yt-dlp] ⚠ Sin m4a para {videoId}: iOS no podrá reproducirlo.");

        // Construir JSON base con relatedStreams vacío
        var result = new
        {
            title = title,
            description = "",
            uploader = uploader,
            uploaderUrl = "",
            uploaderAvatar = "",
            uploaderVerified = false,
            thumbnailUrl = thumbnail,
            duration = durationSecs,
            views = 0L,
            likes = 0L,
            dislikes = 0L,
            hls = (string?)null,
            dash = (string?)null,
            livestream = false,
            proxyUrl = "",
            chapters = Array.Empty<object>(),
            subtitles = Array.Empty<object>(),
            relatedStreams = Array.Empty<object>(),
            previewFrames = Array.Empty<object>(),
            audioStreams = audioStreamsArray,
            videoStreams = Array.Empty<object>()
        };

        // Sin enriquecer: las canciones relacionadas se añaden en segundo plano desde
        // OnStreamResolved, para no retrasar el play.
        return JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    // ─────────────────────────────────────────────────────────────────
    // ESPERA hasta que YouTube sirve de verdad el audio recién extraído
    // ─────────────────────────────────────────────────────────────────
    //
    // Con los clientes por defecto de yt-dlp, cuando un vídeo lleva anuncio previo
    // YouTube no sirve su audio hasta que pasa el tiempo en que el anuncio se puede
    // saltar (unos 5 s): hasta entonces la dirección responde 403. yt-dlp da la hora
    // exacta en "available_at". Con el cliente visionos no hay espera.
    //
    // Con la extracción antigua (tres ejecuciones de yt-dlp, 8-10 s) esto no se notaba:
    // al responder a la app ya había pasado ese tiempo, por casualidad. Al pasar a una
    // sola ejecución la dirección se entregaba antes de hora: el reproductor recibía
    // 403, reintentaba, volvía a recibir 403 y saltaba de canción. Por eso se espera
    // hasta la hora indicada y se comprueba que el audio responde antes de entregarlo.

    // Anuncio saltable: ~5 s. Uno no saltable puede pedir 15 s o más; más allá de este
    // tope no se espera, para no agotar los 25 s que aguanta la app.
    private static readonly TimeSpan MaxAvailabilityWait = TimeSpan.FromSeconds(12);
    // Margen sobre la hora que da yt-dlp: viene en segundos enteros y calculada en la
    // Pi, así que puede quedarse algo corta respecto a la de Google.
    private static readonly TimeSpan AvailabilityMargin = TimeSpan.FromSeconds(1.5);
    private const int PlayableChecks = 2;
    private static readonly TimeSpan PlayableRetryDelay = TimeSpan.FromMilliseconds(800);

    /// <returns>
    /// false solo si el audio sigue respondiendo 403 una vez pasada su hora: esa
    /// dirección no sirve. Otra respuesta o un error de red no se arreglan esperando ni
    /// cambiando de vía, así que cuentan como "no hay nada que objetar".
    /// </returns>
    private async Task<bool> WaitUntilPlayableAsync(string videoId, IReadOnlyList<string> directUrls, DateTimeOffset? availableAt)
    {
        if (directUrls.Count == 0) return true;

        if (availableAt is { } at)
        {
            var wait = at - DateTimeOffset.UtcNow + AvailabilityMargin;
            if (wait > MaxAvailabilityWait)
            {
                Console.WriteLine($"[Stream] ⚠ {videoId}: YouTube pide esperar {wait.TotalSeconds:F0} s (anuncio largo); se espera solo {MaxAvailabilityWait.TotalSeconds:F0} s.");
                wait = MaxAvailabilityWait;
            }
            if (wait > AvailabilityMargin)
                await Task.Delay(wait);
        }

        var client = _httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(5);
        var pending = directUrls.ToList();
        for (int check = 1; check <= PlayableChecks; check++)
        {
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                if (await ProbeStatusAsync(client, pending[i]) != 403)
                    pending.RemoveAt(i);
            }
            if (pending.Count == 0) return true;
            if (check < PlayableChecks) await Task.Delay(PlayableRetryDelay);
        }
        return false;
    }

    /// <summary>Código HTTP al pedir el primer KB del audio (0 = error de red o timeout).</summary>
    private static async Task<int> ProbeStatusAsync(HttpClient client, string url)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
            // Google rechaza las peticiones de audio sin User-Agent de navegador.
            request.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            return (int)response.StatusCode;
        }
        catch
        {
            return 0;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // HELPERS
    // ─────────────────────────────────────────────────────────────────
    private static bool HasValidAudioStreams(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("audioStreams", out var streams))
                return false;
            return streams.GetArrayLength() > 0;
        }
        catch { return false; }
    }

    private static bool HasNonEmptyItems(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("items", out var items))
                return false;
            return items.GetArrayLength() > 0;
        }
        catch { return false; }
    }

    private static string BuildCompatibleJson(
        YoutubeExplode.Videos.Video video,
        IAudioStreamInfo stream,
        IAudioStreamInfo? m4aStream = null)
    {
        // Igual que en la ruta de yt-dlp: el stream de siempre va PRIMERO y con su bitrate
        // real, para que la app Android (maxByOrNull { bitrate }) siga eligiendo lo mismo.
        // El m4a/AAC se agrega detrás solo para iOS, que no puede decodificar Opus/WebM.
        var primary = new
        {
            url = RewriteToOwnProxy(stream.Url),
            format = stream.Container.Name,
            quality = $"{stream.Bitrate.KiloBitsPerSecond:F0}kbps",
            mimeType = $"audio/{stream.Container.Name.ToLower()}",
            codec = "unknown",
            audioTrackId = (string?)null,
            audioTrackName = (string?)null,
            audioTrackType = (string?)null,
            videoOnly = false,
            itag = 0,
            bitrate = (long)stream.Bitrate.BitsPerSecond,
            initStart = 0L,
            initEnd = 0L,
            indexStart = 0L,
            indexEnd = 0L,
            width = 0,
            height = 0,
            fps = 0,
            contentLength = 0L
        };

        var audioStreams = m4aStream == null
            ? new[] { primary }
            : new[]
            {
                primary,
                new
                {
                    url = RewriteToOwnProxy(m4aStream.Url),
                    format = m4aStream.Container.Name,
                    quality = $"{m4aStream.Bitrate.KiloBitsPerSecond:F0}kbps",
                    // Los reproductores esperan audio/mp4 para AAC, no audio/m4a.
                    mimeType = "audio/mp4",
                    codec = "unknown",
                    audioTrackId = (string?)null,
                    audioTrackName = (string?)null,
                    audioTrackType = (string?)null,
                    videoOnly = false,
                    itag = 0,
                    bitrate = (long)m4aStream.Bitrate.BitsPerSecond,
                    initStart = 0L,
                    initEnd = 0L,
                    indexStart = 0L,
                    indexEnd = 0L,
                    width = 0,
                    height = 0,
                    fps = 0,
                    contentLength = 0L
                }
            };

        var result = new
        {
            title = video.Title,
            description = video.Description ?? "",
            uploadDate = video.UploadDate.ToString("yyyy-MM-dd"),
            uploader = video.Author.ChannelTitle,
            uploaderUrl = $"/channel/{video.Author.ChannelId}",
            uploaderAvatar = "",
            uploaderVerified = false,
            thumbnailUrl = video.Thumbnails.OrderByDescending(t => t.Resolution.Area).FirstOrDefault()?.Url ?? "",
            hls = (string?)null,
            dash = (string?)null,
            lbryId = (string?)null,
            category = "Music",
            license = "",
            visibility = "public",
            tags = Array.Empty<string>(),
            metaInfo = Array.Empty<object>(),
            chapters = Array.Empty<object>(),
            audioStreams = audioStreams,
            videoStreams = Array.Empty<object>(),
            relatedStreams = Array.Empty<object>(),
            subtitles = Array.Empty<object>(),
            livestream = false,
            proxyUrl = "",
            previewFrames = Array.Empty<object>(),
            duration = (long)(video.Duration?.TotalSeconds ?? 0),
            views = video.Engagement.ViewCount,
            likes = video.Engagement.LikeCount,
            dislikes = 0L,
            uploaded = ((DateTimeOffset)video.UploadDate).ToUnixTimeMilliseconds()
        };

        return JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    }

    /// <summary>
    /// Reescribe URLs directas de googlevideo.com para que pasen por nuestro
    /// proxy (proxy.emusicmp3.duckdns.org / ytproxy de Piped) en vez de ir
    /// directo al CDN de Google.
    ///
    /// Motivo: Google firma esas URLs atadas a la IP publica que hizo la
    /// extraccion (parametro &ip=). Si el cliente (movil) esta en la misma
    /// red que el servidor (WiFi de casa) funciona porque comparten la
    /// misma IP publica saliente, pero en datos moviles el telefono tiene
    /// una IP distinta y Google devuelve 403 -> la app interpretaba esto
    /// como fin de cancion y saltaba a la siguiente.
    ///
    /// Al pasar por nuestro proxy, es SIEMPRE la Pi quien le pide el audio
    /// a Google (con la IP correcta), sin importar en que red este el
    /// cliente final.
    ///
    /// IMPORTANTE: no se reparsea ni se vuelve a codificar el query string
    /// original (sig/lsig van firmados byte a byte) - solo se cambia el
    /// host y se agrega "&host=" al final, tal como ya lo hace piped-backend.
    /// </summary>
    private static string RewriteToOwnProxy(string url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        if (url.Contains("proxy.emusicmp3.duckdns.org")) return url; // ya viene proxied (Piped)
        if (!url.Contains("googlevideo.com")) return url; // solo nos interesa el CDN de Google

        try
        {
            var schemeIdx = url.IndexOf("://", StringComparison.Ordinal);
            if (schemeIdx < 0) return url;
            var afterScheme = url.Substring(schemeIdx + 3);
            var pathIdx = afterScheme.IndexOf('/');
            if (pathIdx < 0) return url;
            var host = afterScheme.Substring(0, pathIdx);
            var pathAndQuery = afterScheme.Substring(pathIdx); // "/videoplayback?..."

            var separator = pathAndQuery.Contains('?') ? "&" : "?";
            return $"https://proxy.emusicmp3.duckdns.org{pathAndQuery}{separator}host={host}";
        }
        catch
        {
            return url; // si algo sale mal, devolver la original sin tocar
        }
    }

    private static string BuildErrorJson(string message)
    {
        return JsonSerializer.Serialize(new
        {
            error = message,
            audioStreams = Array.Empty<object>(),
            videoStreams = Array.Empty<object>()
        });
    }

    // ─────────────────────────────────────────────────────────────────
    // ENRICHMENT — inyectar relatedStreams en JSON cuando viene vacío
    // ─────────────────────────────────────────────────────────────────
    private async Task<string> EnrichRelatedStreamsIfEmptyAsync(string json, string videoId)
    {
        try
        {
            string artist = "";
            string title  = "";
            using (var docCheck = JsonDocument.Parse(json))
            {
                if (docCheck.RootElement.TryGetProperty("relatedStreams", out var rs) && rs.GetArrayLength() > 0)
                    return json; // Ya tiene, no hace falta enriquecer
                artist = docCheck.RootElement.TryGetProperty("uploader", out var up) ? up.GetString() ?? "" : "";
                title  = docCheck.RootElement.TryGetProperty("title",    out var t)  ? t.GetString()  ?? "" : "";
            }

            // Plan A: Invidious (rápido, pero instancias públicas a menudo caídas)
            var related = await GetRelatedStreamsFromInvidiousAsync(videoId);

            // Plan B: búsqueda por artista usando la misma infraestructura de la API
            if (related.Length == 0)
                related = await GetRelatedStreamsFromSearchAsync(artist, title, videoId);

            if (related.Length == 0) return json;

            // Re-serializar inyectando relatedStreams
            using var docFull = JsonDocument.Parse(json);
            using var ms     = new System.IO.MemoryStream();
            using var writer = new System.Text.Json.Utf8JsonWriter(ms);
            writer.WriteStartObject();
            foreach (var prop in docFull.RootElement.EnumerateObject())
            {
                if (prop.Name == "relatedStreams")
                {
                    writer.WritePropertyName("relatedStreams");
                    JsonSerializer.Serialize(writer, related);
                }
                else
                {
                    prop.WriteTo(writer);
                }
            }
            writer.WriteEndObject();
            writer.Flush();
            var enriched = System.Text.Encoding.UTF8.GetString(ms.ToArray());
            Console.WriteLine($"[Enrich] ✅ relatedStreams inyectados ({related.Length}) para {videoId}");
            return enriched;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Enrich] Error: {ex.Message}");
            return json;
        }
    }

    // Búsqueda por artista como fuente de "related streams" confiable
    private async Task<object[]> GetRelatedStreamsFromSearchAsync(string artist, string title, string excludeVideoId)
    {
        try
        {
            // Nunca buscar solo por título (ej: "Me Gustas" trae canciones random).
            // Siempre incluir artista para mantener el género/estilo.
            if (string.IsNullOrEmpty(artist))
                return Array.Empty<object>();
            var query = $"{artist} mix";

            var searchJson = await SearchAsync(query);
            using var doc = JsonDocument.Parse(searchJson);
            if (!doc.RootElement.TryGetProperty("items", out var items)) return Array.Empty<object>();

            var result = new List<object>();
            foreach (var item in items.EnumerateArray())
            {
                var url = item.TryGetProperty("url", out var urlEl) ? urlEl.GetString() ?? "" : "";
                var idx = url.IndexOf("?v=");
                var vId = idx >= 0 ? url.Substring(idx + 3) : "";
                if (string.IsNullOrEmpty(vId) || vId == excludeVideoId) continue;

                var t      = item.TryGetProperty("title",        out var tEl)   ? tEl.GetString()   ?? "" : "";
                var thumb  = item.TryGetProperty("thumbnail",    out var thEl)  ? thEl.GetString()  ?? "" : "";
                var upl    = item.TryGetProperty("uploaderName", out var upEl)  ? upEl.GetString()  ?? "" : "";
                var dur    = item.TryGetProperty("duration",     out var durEl) && durEl.ValueKind == JsonValueKind.Number
                             ? durEl.GetInt64() : 0L;

                if (string.IsNullOrEmpty(t)) continue;

                result.Add(new {
                    url = $"/watch?v={vId}",
                    videoId = vId,
                    type = "stream",
                    title = t,
                    thumbnail = thumb,
                    uploaderName = upl,
                    uploaderUrl = "",
                    uploaderAvatar = "",
                    uploaderVerified = false,
                    duration = dur,
                    views = 0L,
                    uploaded = 0L,
                    shortDescription = "",
                    isShort = false
                });
                if (result.Count >= 10) break;
            }

            Console.WriteLine($"[Related Search] ✅ {result.Count} resultados para '{query}'");
            return result.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Related Search] Error: {ex.Message}");
            return Array.Empty<object>();
        }
    }

    /// <summary>
    /// Streams relacionados (música similar) a un videoId, en formato JSON
    /// {"items":[...]} compatible con el parser de candidatos. Usado por el
    /// motor de recomendación para descubrir canciones nuevas parecidas a los favoritos.
    /// </summary>
    public async Task<string> GetRelatedAsync(string videoId)
    {
        var cacheKey = $"related_{videoId}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null)
            return cached;

        if (!IsValidVideoId(videoId))
            return "{\"items\":[]}";

        // Antes solo se preguntaba a Invidious, y sus instancias públicas llevan
        // tiempo caídas (403 / sin DNS): el "descubrimiento" del recomendador
        // devolvía siempre cero candidatos. Orden actual:
        //  1. La "radio" que YouTube genera para esa canción (lista RD): canciones
        //     realmente parecidas, con el artista bien puesto. Una petición ligera.
        //  2. Las relacionadas que da Piped local (más ruido: canales de letras,
        //     compilaciones... que luego filtra IsJunkCandidate).
        //  3. Invidious, por si alguna instancia vuelve.
        var related = await GetRelatedFromYouTubeRadioAsync(videoId);
        if (related.Length == 0) related = await GetRelatedFromPipedAsync(videoId);
        if (related.Length == 0) related = await GetRelatedStreamsFromInvidiousAsync(videoId);

        var json = JsonSerializer.Serialize(
            new { items = related },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        // Las canciones parecidas a una dada cambian poco: un día de caché evita
        // repetir la petición a YouTube cada vez que se recalculan recomendaciones.
        if (related.Length > 0)
            _cache.Set(cacheKey, json, TimeSpan.FromHours(24));
        return json;
    }

    /// <summary>Forma común de un candidato relacionado (la que lee ParseCandidates).</summary>
    private static object RelatedItem(string id, string title, string artist, long durationSeconds) => new
    {
        url = $"/watch?v={id}",
        videoId = id,
        type = "stream",
        title,
        thumbnail = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg",
        uploaderName = artist,
        uploaderUrl = "",
        uploaderAvatar = "",
        uploaderVerified = false,
        duration = durationSeconds,
        views = 0L,
        uploaded = 0L,
        shortDescription = "",
        isShort = false
    };

    // ─────────────────────────────────────────────────────────────────
    // RELATED via la "radio" de YouTube (lista RD{videoId}) con yt-dlp
    // ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Canciones de la radio automática de YouTube para un vídeo. No extrae audio
    /// (--flat-playlist): es una sola petición ligera, pero sigue siendo tráfico a
    /// YouTube desde la IP de la Pi, así que respeta el cupo y el cortacircuitos.
    /// </summary>
    private async Task<object[]> GetRelatedFromYouTubeRadioAsync(string videoId)
    {
        if (!_gate.TryConsume())
        {
            Console.WriteLine($"[Radio Related] Omitida para {videoId}: sin cupo de YouTube ahora.");
            return Array.Empty<object>();
        }

        try
        {
            var (_, stdout, stderr, timedOut) = await RunWithTimeoutAsync(
                "yt-dlp",
                new[]
                {
                    "--force-ipv4", "--flat-playlist", "--quiet", "--no-warnings",
                    "--playlist-end", "30",
                    "--print", "%(.{id,title,uploader,channel,duration})j",
                    "--", $"https://www.youtube.com/watch?v={videoId}&list=RD{videoId}"
                },
                YtDlpTimeout);

            if (!timedOut && LooksLikeYouTubeBlock(stderr))
            {
                _gate.ReportBlocked($"al pedir la radio de {videoId}");
                return Array.Empty<object>();
            }

            var items = new List<object>();
            foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;

                    var id = JsonString(root, "id");
                    var title = JsonString(root, "title");
                    if (!IsValidVideoId(id) || id == videoId || title.Length == 0) continue;

                    var artist = JsonString(root, "uploader");
                    if (artist.Length == 0) artist = JsonString(root, "channel");
                    var duration = root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number
                        ? (long)durEl.GetDouble() : 0L;

                    items.Add(RelatedItem(id, title, artist, duration));
                }
                catch { /* línea que no es JSON: se ignora */ }
            }

            Console.WriteLine(items.Count > 0
                ? $"[Radio Related] ✅ {items.Count} canciones parecidas a {videoId}"
                : $"[Radio Related] Sin resultados para {videoId}{(timedOut ? " (timeout)" : "")}");
            return items.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Radio Related] Error para {videoId}: {ex.Message}");
            return Array.Empty<object>();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // RELATED via Piped local (/streams/{id} → relatedStreams)
    // ─────────────────────────────────────────────────────────────────
    /// <summary>
    /// Piped local ya no consigue audio, pero sí devuelve las relacionadas del vídeo.
    /// </summary>
    private async Task<object[]> GetRelatedFromPipedAsync(string videoId)
    {
        if (_gate.IsBlocked(out _)) return Array.Empty<object>(); // Piped sale por la misma IP

        try
        {
            var client = _httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(8);
            var response = await client.GetAsync($"{PipedInstances[0]}/streams/{videoId}");
            if (!response.IsSuccessStatusCode) return Array.Empty<object>();

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("relatedStreams", out var related) ||
                related.ValueKind != JsonValueKind.Array)
                return Array.Empty<object>();

            var items = new List<object>();
            foreach (var v in related.EnumerateArray())
            {
                if (v.ValueKind != JsonValueKind.Object) continue;
                if (JsonString(v, "type") is { Length: > 0 } type && type != "stream") continue;

                var url = JsonString(v, "url");
                var idx = url.IndexOf("?v=", StringComparison.Ordinal);
                var id = idx >= 0 ? url[(idx + 3)..] : "";
                var title = JsonString(v, "title");
                if (!IsValidVideoId(id) || id == videoId || title.Length == 0) continue;

                var duration = v.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number
                    ? (long)durEl.GetDouble() : 0L;
                items.Add(RelatedItem(id, title, JsonString(v, "uploaderName"), duration));
            }

            if (items.Count > 0)
                Console.WriteLine($"[Piped Related] ✅ {items.Count} relacionadas para {videoId}");
            return items.ToArray();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[Piped Related] Fallo para {videoId}: {ex.Message}");
            return Array.Empty<object>();
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // RELATED STREAMS via Invidious /api/v1/videos/{id}
    // ─────────────────────────────────────────────────────────────────
    private async Task<object[]> GetRelatedStreamsFromInvidiousAsync(string videoId)
    {
        foreach (var instance in InvidiousInstances)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(8);
                var url = $"{instance}/api/v1/videos/{videoId}?fields=recommendedVideos";
                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("recommendedVideos", out var recommended)) continue;
                if (recommended.ValueKind != JsonValueKind.Array) continue;

                var items = new List<object>();
                foreach (var v in recommended.EnumerateArray())
                {
                    var id = v.TryGetProperty("videoId", out var idEl) ? idEl.GetString() ?? "" : "";
                    var title = v.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                    var author = v.TryGetProperty("author", out var authEl) ? authEl.GetString() ?? "" : "";
                    var length = v.TryGetProperty("lengthSeconds", out var lenEl) && lenEl.ValueKind == JsonValueKind.Number
                        ? lenEl.GetInt64() : 0L;
                    var views = v.TryGetProperty("viewCount", out var viewEl) && viewEl.ValueKind == JsonValueKind.Number
                        ? viewEl.GetInt64() : 0L;

                    if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(title)) continue;

                    var thumb = $"https://i.ytimg.com/vi/{id}/mqdefault.jpg";
                    items.Add(new
                    {
                        url = $"/watch?v={id}",
                        videoId = id,
                        type = "stream",
                        title,
                        thumbnail = thumb,
                        uploaderName = author,
                        uploaderUrl = "",
                        uploaderAvatar = "",
                        uploaderVerified = false,
                        duration = length,
                        views,
                        uploaded = 0L,
                        shortDescription = "",
                        isShort = false
                    });
                }

                if (items.Count > 0)
                {
                    Console.WriteLine($"[Invidious Related] ✅ {items.Count} related streams para {videoId} via {instance}");
                    return items.ToArray();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Invidious Related] Fallo en {instance}: {ex.Message}");
            }
        }
        Console.WriteLine($"[Invidious Related] Sin resultados para {videoId}");
        return Array.Empty<object>();
    }
}
