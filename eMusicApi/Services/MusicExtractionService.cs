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
                    if (json.Contains("\"items\""))
                    {
                        // Sólo cachear si hay resultados reales
                        _cache.Set(cacheKey, json, TimeSpan.FromMinutes(30));
                        Console.WriteLine($"[Search] OK via {instance}");
                        return json;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Search] Fallo en {instance}: {ex.Message}");
            }
        }

        // Sin resultados de ningún proveedor
        return "{\"items\":[],\"nextpage\":null,\"suggestion\":null,\"corrected\":false}";
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

        return "[]";
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

        // FASE 2: Extracción nativa con YoutubeExplode (Plan B de emergencia)
        Console.WriteLine($"[Stream] Todos los Piped fallaron. Usando YoutubeExplode para {videoId}...");
        return await ExtractWithYoutubeExplodeAsync(videoId, cacheKey);
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
            return BuildErrorJson(ex.Message);
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
        catch
        {
            return false;
        }
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
}
