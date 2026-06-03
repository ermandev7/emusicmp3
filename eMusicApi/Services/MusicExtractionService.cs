using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Caching.Memory;
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

    public MusicExtractionService(IHttpClientFactory httpClientFactory, IMemoryCache cache)
    {
        _httpClientFactory = httpClientFactory;
        _cache = cache;

        // Crear HttpClient con headers de navegador para YoutubeExplode
        var handler = new HttpClientHandler { AllowAutoRedirect = true };
        var httpClient = new HttpClient(handler);
        httpClient.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/124 Safari/537.36");
        _youtubeClient = new YoutubeClient(httpClient);
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
    private static readonly string[] InvidiousInstances =
    [
        "https://inv.nadeko.net",
        "https://invidious.nerdvpn.de",
        "https://yt.artemislena.eu",
        "https://invidious.fdn.fr",
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
        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "yt-dlp",
                Arguments = $"\"{searchArg}\" --flat-playlist -j --quiet --no-warnings --playlist-end 20",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();

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
    // STREAMS — El más crítico: cascada de Piped + YoutubeExplode
    // ─────────────────────────────────────────────────────────────────
    public async Task<string> GetStreamAsync(string videoId)
    {
        var cacheKey = $"stream_{videoId}";
        if (_cache.TryGetValue(cacheKey, out string? cached) && cached != null)
            return cached;

        // FASE 1: Intentar con cada instancia de Piped
        foreach (var instance in PipedInstances)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(10);
                var response = await client.GetAsync($"{instance}/streams/{Uri.EscapeDataString(videoId)}");

                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();

                    // Verificar que el JSON tenga audioStreams válidos y no vacíos
                    if (HasValidAudioStreams(json))
                    {
                        _cache.Set(cacheKey, json, TimeSpan.FromMinutes(55));
                        Console.WriteLine($"[Stream] OK via Piped: {instance}");
                        return json;
                    }
                    Console.WriteLine($"[Stream] {instance} respondió pero sin audioStreams válidos.");
                }
                else
                {
                    Console.WriteLine($"[Stream] {instance} devolvió HTTP {(int)response.StatusCode}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Stream] Fallo en {instance}: {ex.Message}");
            }
        }

        // FASE 2: Extracción nativa con YoutubeExplode (Plan B)
        Console.WriteLine($"[Stream] Todos los Piped fallaron. Intentando YoutubeExplode para {videoId}...");
        var ytExplodeResult = await ExtractWithYoutubeExplodeAsync(videoId, cacheKey);
        if (HasValidAudioStreams(ytExplodeResult))
            return ytExplodeResult;

        // FASE 3: yt-dlp como último recurso (Plan C — el más robusto)
        Console.WriteLine($"[Stream] YoutubeExplode falló. Usando yt-dlp para {videoId}...");
        return await ExtractWithYtDlpAsync(videoId, cacheKey);
    }

    // ─────────────────────────────────────────────────────────────────
    // EXTRACTOR NATIVO con YoutubeExplode
    // ─────────────────────────────────────────────────────────────────
    private async Task<string> ExtractWithYoutubeExplodeAsync(string videoId, string cacheKey)
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

            // Construir JSON compatible con el formato Piped que espera la app MAUI
            var resultJson = BuildCompatibleJson(video, audioStream);
            _cache.Set(cacheKey, resultJson, TimeSpan.FromMinutes(55));
            Console.WriteLine($"[YoutubeExplode] ✅ Stream extraído nativamente para: {video.Title}");
            return resultJson;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[YoutubeExplode] ERROR: {ex.Message}");
            return BuildErrorJson(ex.Message); // Se intentará yt-dlp después
        }
    }

    // ─────────────────────────────────────────────────────────────────
    // EXTRACTOR yt-dlp — Plan C, el más actualizado y robusto
    // ─────────────────────────────────────────────────────────────────
    private async Task<string> ExtractWithYtDlpAsync(string videoId, string cacheKey)
    {
        try
        {
            // Obtener URL directa de audio con yt-dlp
            var urlProcess = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"--format bestaudio --get-url --no-playlist --quiet https://www.youtube.com/watch?v={videoId}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            urlProcess.Start();
            var audioUrl = (await urlProcess.StandardOutput.ReadToEndAsync()).Trim();
            var errorOut = await urlProcess.StandardError.ReadToEndAsync();
            await urlProcess.WaitForExitAsync();

            if (urlProcess.ExitCode != 0 || string.IsNullOrEmpty(audioUrl))
            {
                Console.WriteLine($"[yt-dlp] ERROR: {errorOut}");
                return BuildErrorJson($"yt-dlp falló: {errorOut}");
            }

            // Obtener título, duración (en segundos) y thumbnail con yt-dlp
            var metaProcess = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "yt-dlp",
                    Arguments = $"--print title --print duration --print thumbnail --no-playlist --quiet https://www.youtube.com/watch?v={videoId}",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            metaProcess.Start();
            var metaLines = (await metaProcess.StandardOutput.ReadToEndAsync()).Split('\n');
            await metaProcess.WaitForExitAsync();

            var title = metaLines.Length > 0 ? metaLines[0].Trim() : videoId;
            var durationSecs = metaLines.Length > 1 && double.TryParse(metaLines[1].Trim(),
                System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? (long)d : 0L;
            var thumbnail = metaLines.Length > 2 ? metaLines[2].Trim() : "";

            // Obtener related streams via Invidious
            var relatedStreams = await GetRelatedStreamsFromInvidiousAsync(videoId);

            // Construir JSON compatible con formato Piped
            var result = new
            {
                title = title,
                description = "",
                uploader = "",
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
                relatedStreams = relatedStreams,
                previewFrames = Array.Empty<object>(),
                audioStreams = new[]
                {
                    new
                    {
                        url = audioUrl,
                        format = "webm",
                        quality = "bestaudio",
                        mimeType = "audio/webm",
                        bitrate = 128000L,
                        videoOnly = false,
                        itag = 0,
                        codec = "opus"
                    }
                },
                videoStreams = Array.Empty<object>()
            };

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            _cache.Set(cacheKey, json, TimeSpan.FromMinutes(50));
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

    private static string BuildCompatibleJson(YoutubeExplode.Videos.Video video, IAudioStreamInfo stream)
    {
        var audioStreams = new[]
        {
            new
            {
                url = stream.Url,
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
