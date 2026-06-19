using System.Text.Json;
using eMusicApi.Data;
using eMusicApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class RecommendationController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly MusicExtractionService _music;
    private readonly RecommendationEngine _engine;
    private readonly IMemoryCache _cache;

    public RecommendationController(
        AppDbContext db,
        MusicExtractionService music,
        RecommendationEngine engine,
        IMemoryCache cache)
    {
        _db = db;
        _music = music;
        _engine = engine;
        _cache = cache;
    }

    /// <summary>
    /// GET /api/recommendation?limit=20
    /// Construye perfil del usuario desde su historial, busca candidatos,
    /// los puntúa con TF-IDF + cosine similarity y devuelve los mejores.
    /// Resultado cacheado 30 minutos.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int limit = 20)
    {
        var userId = Request.Headers.TryGetValue("X-User-Id", out var val) ? val.ToString() : "";
        var cacheKey = $"reco:{userId}:{limit}";

        if (_cache.TryGetValue(cacheKey, out string? cached))
            return Content(cached!, "application/json");

        var history = await _db.History
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.PlayedAt)
            .Take(200)
            .AsNoTracking()
            .ToListAsync();

        var favorites = await _db.Favorites
            .Where(f => f.UserId == userId)
            .AsNoTracking()
            .ToListAsync();

        if (history.Count + favorites.Count < 3)
            return Ok(new { items = Array.Empty<object>(), message = "Escucha o marca como favoritas al menos 3 canciones" });

        // Exclusiones ("no recomendar") del usuario.
        var exclusions = await _db.Exclusions
            .Where(e => e.UserId == userId)
            .AsNoTracking()
            .ToListAsync();
        var excludedIds = exclusions
            .Select(e => e.VideoId).Where(v => !string.IsNullOrEmpty(v)).ToHashSet();
        var excludedArtists = exclusions
            .Select(e => RecommendationEngine.NormalizeArtist(e.Artist))
            .Where(a => !string.IsNullOrEmpty(a)).ToHashSet();

        var profile = _engine.BuildProfile(history, favorites);
        var queries = _engine.GenerateSearchQueries(profile);

        // Concurrencia limitada para no saturar la Pi.
        var semaphore = new SemaphoreSlim(3);

        // FAMILIAR: búsquedas por artistas/géneros del perfil.
        var searchTasks = queries.Select(async q =>
        {
            await semaphore.WaitAsync();
            try { return await _music.SearchAsync(q); }
            catch { return "[]"; }
            finally { semaphore.Release(); }
        });

        // DESCUBRIMIENTO: related streams de las semillas (favoritos primero, luego historial).
        var seedIds = favorites.Select(f => f.Id)
            .Concat(history.Select(h => h.VideoId))
            .Where(v => !string.IsNullOrEmpty(v))
            .Distinct()
            .Take(3);
        var relatedTasks = seedIds.Select(async id =>
        {
            await semaphore.WaitAsync();
            try { return await _music.GetRelatedAsync(id); }
            catch { return "[]"; }
            finally { semaphore.Release(); }
        });

        var allResults = await Task.WhenAll(searchTasks.Concat(relatedTasks));

        var candidates = ParseCandidates(allResults, profile.PlayedVideoIds)
            .Where(c => !excludedIds.Contains(c.VideoId) &&
                        !excludedArtists.Contains(RecommendationEngine.NormalizeArtist(c.Artist)))
            .ToList();

        var scored = candidates
            .Select(c => new
            {
                url = c.Url,
                title = c.Title,
                uploaderName = c.Artist,
                thumbnail = c.Thumb,
                duration = c.Duration,
                type = "stream",
                score = Math.Round(_engine.ScoreCandidate(profile, c.Title, c.Artist), 4)
            })
            .OrderByDescending(c => c.score)
            .ToList();

        // DIVERSIDAD: máximo 2 canciones por artista en el resultado final.
        var perArtist = new Dictionary<string, int>();
        var diversified = new List<object>();
        foreach (var c in scored)
        {
            var key = RecommendationEngine.NormalizeArtist(c.uploaderName);
            perArtist.TryGetValue(key, out int cnt);
            if (cnt >= 2) continue;
            perArtist[key] = cnt + 1;
            diversified.Add(c);
            if (diversified.Count >= limit) break;
        }

        var result = JsonSerializer.Serialize(new { items = diversified });
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(30));

        return Content(result, "application/json");
    }

    /// <summary>
    /// GET /api/recommendation/profile
    /// Devuelve el perfil calculado del usuario (debug/transparencia).
    /// </summary>
    [HttpGet("profile")]
    public async Task<IActionResult> GetProfile()
    {
        var userId = Request.Headers.TryGetValue("X-User-Id", out var val) ? val.ToString() : "";

        var history = await _db.History
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.PlayedAt)
            .Take(200)
            .AsNoTracking()
            .ToListAsync();

        var favorites = await _db.Favorites
            .Where(f => f.UserId == userId)
            .AsNoTracking()
            .ToListAsync();

        if (history.Count == 0 && favorites.Count == 0)
            return Ok(new { message = "Sin historial ni favoritos" });

        var profile = _engine.BuildProfile(history, favorites);

        return Ok(new
        {
            topArtists = profile.TopArtists.Select(a => new { artist = a.Key, weight = Math.Round(a.Value, 2) }),
            topGenres = profile.TopGenres.Select(g => new { genre = g.Key, weight = Math.Round(g.Value, 2) }),
            favoriteArtists = profile.FavoriteArtists,
            topTokens = profile.TokenVector
                .OrderByDescending(kv => kv.Value)
                .Take(15)
                .Select(kv => new { token = kv.Key, weight = Math.Round(kv.Value, 2) }),
            tracksAnalyzed = history.Count,
            favoritesAnalyzed = favorites.Count,
            searchQueries = _engine.GenerateSearchQueries(profile)
        });
    }

    /// <summary>
    /// POST /api/recommendation/exclude  — marca una canción/artista como "no recomendar".
    /// </summary>
    [HttpPost("exclude")]
    public async Task<IActionResult> Exclude([FromBody] ExcludeRequest request)
    {
        var userId = Request.Headers.TryGetValue("X-User-Id", out var val) ? val.ToString() : "";
        if (request == null || string.IsNullOrEmpty(request.VideoId))
            return BadRequest();

        bool exists = await _db.Exclusions
            .AnyAsync(e => e.UserId == userId && e.VideoId == request.VideoId);
        if (!exists)
        {
            _db.Exclusions.Add(new eMusicApi.Models.Exclusion
            {
                UserId = userId,
                VideoId = request.VideoId,
                Artist = request.Artist ?? "",
                CreatedAt = DateTime.UtcNow
            });
            await _db.SaveChangesAsync();
        }

        // Invalidar cache de recomendaciones del usuario.
        foreach (var lim in new[] { 10, 20, 30, 50 })
            _cache.Remove($"reco:{userId}:{lim}");

        return Ok(new { excluded = request.VideoId });
    }

    private static List<(string VideoId, string Title, string Artist, string Thumb, int Duration, string Url)>
        ParseCandidates(string[] searchResults, HashSet<string> excludeIds)
    {
        var candidates = new List<(string, string, string, string, int, string)>();
        var seenIds = new HashSet<string>(excludeIds);

        foreach (var json in searchResults)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                JsonElement items;
                if (root.ValueKind == JsonValueKind.Array)
                    items = root;
                else if (root.TryGetProperty("items", out var ip))
                    items = ip;
                else continue;

                foreach (var el in items.EnumerateArray())
                {
                    var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "stream";
                    if (type != "stream") continue;

                    var videoId = ExtractVideoId(el);
                    if (string.IsNullOrEmpty(videoId) || !seenIds.Add(videoId)) continue;

                    var title = el.TryGetProperty("title", out var titlep) ? titlep.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(title)) continue;

                    var artist = el.TryGetProperty("uploaderName", out var up) ? up.GetString() ?? ""
                               : el.TryGetProperty("uploader", out var up2) ? up2.GetString() ?? "" : "";
                    var thumb = el.TryGetProperty("thumbnail", out var thp) ? thp.GetString() ?? ""
                              : el.TryGetProperty("thumbnailUrl", out var thp2) ? thp2.GetString() ?? "" : "";
                    var duration = el.TryGetProperty("duration", out var dp) && dp.ValueKind == JsonValueKind.Number
                        ? dp.GetInt32() : 0;
                    var url = el.TryGetProperty("url", out var tup) ? tup.GetString() ?? $"/watch?v={videoId}" : $"/watch?v={videoId}";

                    candidates.Add((videoId, title, artist, thumb, duration, url));
                }
            }
            catch { }
        }

        return candidates;
    }

    private static string? ExtractVideoId(JsonElement el)
    {
        if (el.TryGetProperty("videoId", out var vid))
            return vid.GetString();
        if (el.TryGetProperty("url", out var urlp))
        {
            var url = urlp.GetString() ?? "";
            var idx = url.IndexOf("?v=");
            if (idx >= 0) return url.Substring(idx + 3);
        }
        return null;
    }
}

public class ExcludeRequest
{
    public string VideoId { get; set; } = "";
    public string? Artist { get; set; }
    public string? Title { get; set; }
}
