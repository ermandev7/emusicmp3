using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System;
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
/// Cascada: Piped Principal → 3 Piped Públicos → YoutubeExplode Nativo.
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
    /// </summary>
    private static async Task<(int exitCode, string stdout, string stderr, bool timedOut)> RunWithTimeoutAsync(
        string fileName, string arguments, TimeSpan timeout)
    {
        using var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

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
    public async Task<string> SearchAsync(string query)
    {
        var cacheKey = $"search_{query.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null)
            return cached;

        foreach (var instance in PipedInstances)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(8);
                var url = $"{instance}/search?q={Uri.EscapeDataString(query)}&filter=music_songs";
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
        var (_, output, _, timedOut) = await RunWithTimeoutAsync(
            "yt-dlp",
            $"\"{searchArg}\" --force-ipv4 --flat-playlist -j --quiet --no-warnings --playlist-end 20",
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

        // Lanzar TODOS los extractores en paralelo — el primero que devuelva
        // audioStreams válidos gana. yt-dlp es el más robusto (casi nunca falla),
        // pero Piped interno puede ser más rápido si está funcionando.
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var tasks = new List<Task<(string json, string source)>>();

        // yt-dlp: el más robusto, casi siempre funciona. Se acota con el cts general
        // para que, aunque el proceso tarde, esta rama del Task.WhenAny no bloquee
        // más allá del límite común (el proceso en sí se mata por su cuenta si se
        // pasa de YtDlpTimeout, ver RunWithTimeoutAsync).
        tasks.Add(BoundedBySource(Task.Run(async () =>
        {
            var json = await ExtractWithYtDlpAsync(videoId, cacheKey: null);
            return (json, "yt-dlp");
        }), "yt-dlp", cts.Token));

        // Piped interno (Docker) — el más rápido cuando funciona
        tasks.Add(TryPipedInstanceAsync(PipedInstances[0], videoId, cts.Token));

        // YoutubeExplode — Plan B nativo
        tasks.Add(BoundedBySource(Task.Run(async () =>
        {
            var json = await ExtractWithYoutubeExplodeAsync(videoId, cacheKey: null);
            return (json, "YoutubeExplode");
        }), "YoutubeExplode", cts.Token));

        // 1-2 Piped públicos (solo los que resuelven DNS)
        if (PipedInstances.Length > 1)
            tasks.Add(TryPipedInstanceAsync(PipedInstances[1], videoId, cts.Token));
        if (PipedInstances.Length > 2)
            tasks.Add(TryPipedInstanceAsync(PipedInstances[2], videoId, cts.Token));

        // Esperar al primero que tenga audioStreams válidos
        while (tasks.Count > 0)
        {
            var completed = await Task.WhenAny(tasks);
            tasks.Remove(completed);

            try
            {
                var (json, source) = await completed;
                if (HasValidAudioStreams(json))
                {
                    _cache.Set(cacheKey, json, TimeSpan.FromMinutes(55));
                    Console.WriteLine($"[Stream] OK via {source} (paralelo) para {videoId}");
                    cts.Cancel(); // Cancelar el resto
                    // Enriquecer related streams en background (no bloquea el play) y
                    // precalentar (prefetch) las 2 canciones mas probables de sonar despues.
                    // Arregla el caso "una cancion tarda 4-5s en resolver (p.ej. necesita
                    // yt-dlp porque Piped no le saca audio) y el reproductor la salta por
                    // tardar": si ya llego precalentada desde que sonaba la anterior, el
                    // play es instantaneo sin importar cuanto tarde esa cancion en concreto.
                    // Se aplica siempre, para cualquier cancion, no solo para las que ya
                    // sabemos que son lentas.
                    _ = Task.Run(async () =>
                    {
                        var enriched = await EnrichRelatedStreamsIfEmptyAsync(json, videoId);
                        if (enriched != json)
                            _cache.Set(cacheKey, enriched, TimeSpan.FromMinutes(55));

                        // Solo desde una reproduccion real: si esta llamada YA es un
                        // precalentado, no encadenar mas (evita una cascada sin limite que
                        // multiplica las peticiones a YouTube y dispara su bloqueo por
                        // "demasiadas peticiones" — nos paso el 29/09, ver commit).
                        if (!isPrefetch)
                        {
                            foreach (var relatedId in ExtractRelatedVideoIds(enriched, max: 2))
                            {
                                if (relatedId == videoId) continue;
                                if (_cache.TryGetValue($"stream_{relatedId}", out string? _)) continue; // ya calentada
                                _ = GetStreamAsync(relatedId, allowSubstitute: true, isPrefetch: true); // fire-and-forget, un solo nivel
                            }
                        }
                    });
                    return json;
                }
                Console.WriteLine($"[Stream] {source} respondió sin audioStreams válidos.");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Stream] Extractor falló: {ex.Message}");
            }
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
        _cache.Set(failKey, failureJson, StreamFailureCooldown);
        Console.WriteLine($"[Stream] {videoId}: todos los extractores fallaron (sin sustituto disponible); en enfriamiento {StreamFailureCooldown.TotalMinutes:F0} min.");
        return failureJson;
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
    /// URL directa del primer formato que matchee <paramref name="formatSelector"/>.
    /// Devuelve null si yt-dlp falla o si ese formato no existe para el vídeo, para que
    /// el llamador pueda seguir sin él (no lanza).
    /// </summary>
    private static async Task<string?> RunYtDlpGetUrlAsync(string formatSelector, string videoId)
    {
        try
        {
            var (exitCode, output, _, timedOut) = await RunWithTimeoutAsync(
                "yt-dlp",
                $"--force-ipv4 --format {formatSelector} --get-url --no-playlist --quiet https://www.youtube.com/watch?v={videoId}",
                YtDlpTimeout);
            if (timedOut)
                Console.WriteLine($"[yt-dlp {formatSelector}] ⏱ Timeout para {videoId}; proceso detenido.");
            var trimmed = output.Trim();
            return exitCode == 0 && !string.IsNullOrWhiteSpace(trimmed) ? trimmed : null;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[yt-dlp {formatSelector}] {ex.Message}");
            return null;
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // EXTRACTOR yt-dlp — Plan C, el más actualizado y robusto
    // ─────────────────────────────────────────────────────────────────
    private async Task<string> ExtractWithYtDlpAsync(string videoId, string? cacheKey)
    {
        try
        {
            // El m4a (AAC) se pide EN PARALELO con el bestaudio de siempre.
            // Motivo: `bestaudio` en YouTube es Opus/WebM, que ExoPlayer (Android)
            // decodifica sin problema pero AVFoundation (iOS) no soporta en absoluto.
            // Se lanza antes que nada para que no sume latencia al play.
            var m4aUrlTask = RunYtDlpGetUrlAsync("bestaudio[ext=m4a]", videoId);

            // Obtener URL directa de audio con yt-dlp (con límite de tiempo real: si
            // YouTube exige resolver un reto JS y yt-dlp se queda esperando, esto lo
            // mata en vez de dejar la petición colgada indefinidamente)
            var (urlExitCode, audioUrlRaw, errorOut, urlTimedOut) = await RunWithTimeoutAsync(
                "yt-dlp",
                $"--force-ipv4 --format bestaudio --get-url --no-playlist --quiet https://www.youtube.com/watch?v={videoId}",
                YtDlpTimeout);
            var audioUrl = audioUrlRaw.Trim();

            if (urlTimedOut)
                Console.WriteLine($"[yt-dlp] ⏱ Timeout obteniendo audio de {videoId}; proceso detenido.");
            if (urlExitCode != 0 || string.IsNullOrEmpty(audioUrl))
            {
                Console.WriteLine($"[yt-dlp] ERROR: {errorOut}");
                return BuildErrorJson($"yt-dlp falló: {errorOut}");
            }

            // Obtener título, duración (en segundos), thumbnail y canal con yt-dlp.
            // El canal/artista es imprescindible para poder buscar canciones relacionadas
            // despues (EnrichRelatedStreamsIfEmptyAsync se niega a buscar sin artista, para
            // no traer resultados aleatorios) — sin esto, las canciones que necesitan yt-dlp
            // (las mas propensas a fallar en Piped) nunca se beneficiaban del precalentado.
            var (_, metaOutput, _, metaTimedOut) = await RunWithTimeoutAsync(
                "yt-dlp",
                $"--force-ipv4 --print title --print duration --print thumbnail --print uploader --no-playlist --quiet https://www.youtube.com/watch?v={videoId}",
                YtDlpTimeout);
            if (metaTimedOut)
                Console.WriteLine($"[yt-dlp] ⏱ Timeout obteniendo metadatos de {videoId}; se usarán valores por defecto.");
            var metaLines = metaOutput.Split('\n');

            var title = metaLines.Length > 0 ? metaLines[0].Trim() : videoId;
            var durationSecs = metaLines.Length > 1 && double.TryParse(metaLines[1].Trim(),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? (long)d : 0L;
            var thumbnail = metaLines.Length > 2 ? metaLines[2].Trim() : "";
            var uploader = metaLines.Length > 3 ? metaLines[3].Trim() : "";
            if (uploader == "NA") uploader = "";

            // El WebM/Opus va SIEMPRE PRIMERO y con el mismo bitrate declarado que antes.
            // La app Android elige con maxByOrNull { bitrate }, que ante empate se queda con
            // el primer elemento: su comportamiento no cambia en absoluto y el APK ya
            // distribuido sigue funcionando igual. El m4a se agrega solo para iOS, que no
            // puede decodificar Opus.
            var m4aUrl = await m4aUrlTask;
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

            // Serializar sin enriquecer (se hace en background desde GetStreamAsync)
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            if (cacheKey != null)
            {
                json = await EnrichRelatedStreamsIfEmptyAsync(json, videoId);
                _cache.Set(cacheKey, json, TimeSpan.FromMinutes(50));
            }
            Console.WriteLine($"[yt-dlp] ✅ Audio extraído exitosamente: {title}");
            return json;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[yt-dlp] EXCEPCIÓN: {ex.Message}");
            return BuildErrorJson($"Todos los extractores fallaron: {ex.Message}");
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

        var related = await GetRelatedStreamsFromInvidiousAsync(videoId);
        var json = JsonSerializer.Serialize(
            new { items = related },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        if (related.Length > 0)
            _cache.Set(cacheKey, json, TimeSpan.FromHours(2));
        return json;
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
