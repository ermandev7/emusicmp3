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

    // Canciones del historial con las que se construye el perfil. Antes 200: un usuario
    // activo llegaba al tope y cada canción nueva expulsaba una antigua, aunque fuera
    // de las más escuchadas (ver también el recorte en HistoryController).
    private const int HistoryForProfile = 2000;

    // Canciones de partida para el descubrimiento en cada recálculo.
    private const int SeedCount = 6;

    /// <summary>
    /// GET /api/recommendation?limit=20
    /// Construye el perfil del usuario desde su historial y favoritos, reúne candidatos
    /// de dos fuentes (búsquedas por sus artistas/géneros y canciones parecidas a una
    /// selección rotativa de las suyas) y devuelve una mezcla de artistas conocidos y
    /// nuevos. Resultado cacheado 30 minutos.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] int limit = 20)
    {
        var userId = Request.Headers.TryGetValue("X-User-Id", out var val) ? val.ToString() : "";
        limit = Math.Clamp(limit, 1, 100);
        var cacheKey = $"reco:{userId}:{limit}";

        if (_cache.TryGetValue(cacheKey, out string? cached))
            return Content(cached!, "application/json");

        var history = await _db.History
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.PlayedAt)
            .Take(HistoryForProfile)
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

        var rng = new Random();
        var profile = _engine.BuildProfile(history, favorites, excludedArtists);
        var queries = _engine.GenerateSearchQueries(profile);
        var seeds = _engine.PickSeeds(history, favorites, excludedArtists, SeedCount, rng);

        // Concurrencia limitada para no saturar la Pi.
        var semaphore = new SemaphoreSlim(3);

        // FAMILIAR: búsquedas por artistas/géneros del perfil.
        var searchTasks = queries.Select(async q =>
        {
            await semaphore.WaitAsync();
            try { return await _music.SearchAsync(q); }
            catch { return "[]"; }
            finally { semaphore.Release(); }
        }).ToList();

        // DESCUBRIMIENTO: canciones parecidas a cada semilla.
        var relatedTasks = seeds.Select(async seed =>
        {
            await semaphore.WaitAsync();
            try { return (seed, json: await _music.GetRelatedAsync(seed.VideoId)); }
            catch { return (seed, json: "[]"); }
            finally { semaphore.Release(); }
        }).ToList();

        var searchResults = await Task.WhenAll(searchTasks);
        var relatedResults = await Task.WhenAll(relatedTasks);

        // Un candidato por videoId. Los de búsqueda entran con Discovery = 0; los que
        // salen en las parecidas de una semilla suman el peso relativo de esa semilla,
        // atenuado por su posición en la lista (el 1º cuenta 1, el 9º la mitad).
        var byId = new Dictionary<string, Candidate>();
        foreach (var json in searchResults)
            foreach (var c in ParseItems(json))
                byId.TryAdd(c.VideoId, c);

        double seedWeightTotal = seeds.Sum(s => s.Weight);
        int seedsWithResults = 0;
        foreach (var (seed, json) in relatedResults)
        {
            int position = 0;
            foreach (var c in ParseItems(json))
            {
                if (!byId.TryGetValue(c.VideoId, out var candidate))
                    byId[c.VideoId] = candidate = c;
                double seedShare = seedWeightTotal > 0 ? seed.Weight / seedWeightTotal : 0;
                candidate.Discovery += seedShare / (1 + position / 8.0);
                position++;
            }
            if (position > 0) seedsWithResults++;
        }

        // Lo escuchado hace poco, por título: otra subida u otra versión de la misma
        // canción no es una recomendación nueva (misma ventana que los videoIds).
        var recentKeys = RecommendationEngine.RecentSongKeys(history);

        // "No recomendar" a un artista vale también para sus canciones subidas por otro
        // canal, donde el artista solo aparece en el título.
        bool IsExcluded(Candidate c)
        {
            var na = RecommendationEngine.NormalizeArtist(c.Artist);
            var lowerTitle = c.Title.ToLowerInvariant();
            return excludedArtists.Any(ex =>
                (na.Length > 0 && RecommendationEngine.ArtistMatches(ex, na)) ||
                RecommendationEngine.TitleMentionsArtist(lowerTitle, ex));
        }

        var candidates = byId.Values
            .Where(c => !profile.PlayedVideoIds.Contains(c.VideoId) &&
                        !profile.SkippedVideoIds.Contains(c.VideoId) &&
                        !excludedIds.Contains(c.VideoId) &&
                        !IsExcluded(c) &&
                        !RecommendationEngine.IsJunkCandidate(c.Title, c.Duration) &&
                        !(RecommendationEngine.SongKey(c.Title, c.Artist) is { } k && recentKeys.Contains(k)))
            .ToList();

        var ranked = _engine.Rank(profile, candidates, limit, rng);

        Console.WriteLine(
            $"[Reco] {ranked.Count} recomendaciones ({ranked.Count(r => r.IsNewArtist)} de artistas nuevos) " +
            $"de {candidates.Count} candidatos; semillas con resultados: {seedsWithResults}/{seeds.Count}.");

        var items = ranked.Select(r => new
        {
            url = r.Candidate.Url,
            title = r.Candidate.Title,
            uploaderName = r.Candidate.Artist,
            thumbnail = r.Candidate.Thumb,
            duration = r.Candidate.Duration,
            type = "stream",
            score = r.Score
        });

        var result = JsonSerializer.Serialize(new { items });
        // Si YouTube no dejó consultar ninguna semilla (cupo agotado o bloqueo), la lista
        // sale sin descubrimiento: se guarda poco tiempo para reintentarlo pronto.
        var cacheFor = seeds.Count > 0 && seedsWithResults == 0 ? TimeSpan.FromMinutes(3) : TimeSpan.FromMinutes(30);
        _cache.Set(cacheKey, result, cacheFor);

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
            .Take(HistoryForProfile)
            .AsNoTracking()
            .ToListAsync();

        var favorites = await _db.Favorites
            .Where(f => f.UserId == userId)
            .AsNoTracking()
            .ToListAsync();

        if (history.Count == 0 && favorites.Count == 0)
            return Ok(new { message = "Sin historial ni favoritos" });

        var excludedArtists = (await _db.Exclusions
                .Where(e => e.UserId == userId)
                .AsNoTracking()
                .ToListAsync())
            .Select(e => RecommendationEngine.NormalizeArtist(e.Artist))
            .Where(a => !string.IsNullOrEmpty(a)).ToHashSet();

        var profile = _engine.BuildProfile(history, favorites, excludedArtists);

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
        foreach (var lim in new[] { 10, 20, 30, 40, 50, 60, 80, 100 })
            _cache.Remove($"reco:{userId}:{lim}");

        return Ok(new { excluded = request.VideoId });
    }

    /// <summary>Candidatos de UNA respuesta ({"items":[...]} o [...]), en su orden original.</summary>
    private static List<Candidate> ParseItems(string json)
    {
        var candidates = new List<Candidate>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            JsonElement items;
            if (root.ValueKind == JsonValueKind.Array)
                items = root;
            else if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("items", out var ip))
                items = ip;
            else return candidates;

            foreach (var el in items.EnumerateArray())
            {
                var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "stream";
                if (type != "stream") continue;

                var videoId = ExtractVideoId(el);
                if (string.IsNullOrEmpty(videoId)) continue;

                var title = el.TryGetProperty("title", out var titlep) ? titlep.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(title)) continue;

                var artist = el.TryGetProperty("uploaderName", out var up) ? up.GetString() ?? ""
                           : el.TryGetProperty("uploader", out var up2) ? up2.GetString() ?? "" : "";
                var thumb = el.TryGetProperty("thumbnail", out var thp) ? thp.GetString() ?? ""
                          : el.TryGetProperty("thumbnailUrl", out var thp2) ? thp2.GetString() ?? "" : "";
                var duration = el.TryGetProperty("duration", out var dp) && dp.ValueKind == JsonValueKind.Number
                    ? (int)Math.Clamp(dp.GetDouble(), 0, int.MaxValue) : 0;
                var url = el.TryGetProperty("url", out var tup) ? tup.GetString() ?? $"/watch?v={videoId}" : $"/watch?v={videoId}";

                candidates.Add(new Candidate
                {
                    VideoId = videoId, Title = title, Artist = artist, Thumb = thumb, Duration = duration, Url = url
                });
            }
        }
        catch { /* respuesta que no es el JSON esperado: sin candidatos */ }

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
