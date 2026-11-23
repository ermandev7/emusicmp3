using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using eMusicApi.Data;
using Microsoft.EntityFrameworkCore;

namespace eMusicApi.Services;

/// <summary>
/// Lo que suena al pulsar un botón de género en la app.
///
/// Antes el botón era una búsqueda fija ("cumbia éxitos Los Ángeles Azules Grupo 5 mix
/// bailable") y la app reproducía el primer resultado: lo mismo para todo el mundo, y
/// casi siempre una canción suelta. Ahora se reúnen mixes de ese género y se ordenan
/// con el perfil del usuario (RecommendationEngine.RankGenre): primero los mixes que
/// nombran a sus artistas, sin lo que excluyó o saltó, y rotando lo que sonó hace poco.
///
/// La respuesta tiene el mismo formato que una búsqueda, así que la app no cambia.
/// </summary>
public class GenreRadioService
{
    // Mismo historial que usa el recomendador general.
    private const int HistoryForProfile = 2000;

    private readonly AppDbContext _db;
    private readonly MusicExtractionService _music;
    private readonly RecommendationEngine _engine;

    public GenreRadioService(AppDbContext db, MusicExtractionService music, RecommendationEngine engine)
    {
        _db = db;
        _music = music;
        _engine = engine;
    }

    private sealed record PoolItem(JsonElement Raw, GenreCandidate Candidate);

    /// <returns>
    /// JSON con forma de respuesta de búsqueda ({"items":[...]}), o null si no se pudo
    /// reunir nada (el llamador hace entonces la búsqueda normal de siempre).
    /// </returns>
    public async Task<string?> BuildAsync(string userId, GenreStation station)
    {
        var history = await _db.History
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.PlayedAt)
            .Take(HistoryForProfile)
            .AsNoTracking()
            .ToListAsync();
        var favorites = await _db.Favorites.Where(f => f.UserId == userId).AsNoTracking().ToListAsync();
        var exclusions = await _db.Exclusions.Where(e => e.UserId == userId).AsNoTracking().ToListAsync();

        var excludedIds = exclusions.Select(e => e.VideoId).Where(v => !string.IsNullOrEmpty(v)).ToHashSet();
        var excludedArtists = exclusions
            .Select(e => RecommendationEngine.NormalizeArtist(e.Artist))
            .Where(a => a.Length > 0).ToHashSet();

        // Usuario sin historial: el perfil sale vacío y el orden lo deciden "es un mix"
        // y la relevancia de la búsqueda. Sigue siendo mejor que una canción suelta.
        var profile = _engine.BuildProfile(history, favorites, excludedArtists);

        var year = DateTime.UtcNow.Year.ToString();
        var queries = station.MixQueries
            .Select(q => q with { Text = q.Text.Replace("{year}", year) })
            // La búsqueda original del botón, como red de seguridad: si las de mixes no
            // devolvieran nada, al menos queda lo que sonaba antes.
            .Append(new GenreQuery(station.AppQuery, MusicExtractionService.DefaultSearchFilter))
            .ToList();

        var pool = new Dictionary<string, PoolItem>();
        await AddToPoolAsync(pool, queries);

        // Solo se usan mixes DEL GÉNERO. Se probó añadir además los mixes propios de los
        // artistas del usuario nombrados en ellos ("{artista} mix"), y se descartó: no
        // hay forma fiable de saber de qué género es un artista, y con un perfil real el
        // botón "Pop" acababa abriendo con un álbum de bachata. La personalización está
        // en el orden: primero los mixes del género que nombran a sus artistas.

        var candidates = pool.Values
            .Select(p => p.Candidate)
            .Where(c => !excludedIds.Contains(c.VideoId) &&
                        !excludedArtists.Any(ex => RecommendationEngine.MentionsArtist(c.Title, c.Artist, ex)))
            .ToList();

        var favoriteIds = favorites.Select(f => f.Id).ToHashSet();
        var ranked = _engine.RankGenre(profile, candidates, favoriteIds, new Random());
        if (ranked.Count == 0) return null;

        var first = ranked[0];
        Console.WriteLine(
            $"[Género] {station.Key}: {ranked.Count} resultados ({ranked.Count(r => r.IsMix)} mixes) de {pool.Count} candidatos. " +
            $"Primero: \"{first.Candidate.Title}\" ({first.Candidate.Duration / 60} min, score {first.Score:F2}).");

        return JsonSerializer.Serialize(new
        {
            items = ranked.Select(r => pool[r.Candidate.VideoId].Raw),
            nextpage = (string?)null,
            suggestion = (string?)null,
            corrected = false
        });
    }

    /// <summary>Lanza las búsquedas (máx. 3 a la vez) y añade sus resultados al conjunto.</summary>
    private async Task AddToPoolAsync(Dictionary<string, PoolItem> pool, List<GenreQuery> queries)
    {
        using var semaphore = new SemaphoreSlim(3);
        var results = await Task.WhenAll(queries.Select(async q =>
        {
            await semaphore.WaitAsync();
            try { return await _music.SearchAsync(q.Text, q.Filter); }
            catch { return "{}"; }
            finally { semaphore.Release(); }
        }));

        foreach (var json in results)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                    !doc.RootElement.TryGetProperty("items", out var items) ||
                    items.ValueKind != JsonValueKind.Array)
                    continue;

                int position = 0;
                foreach (var el in items.EnumerateArray())
                {
                    if (el.ValueKind != JsonValueKind.Object) continue;
                    var type = el.TryGetProperty("type", out var tp) ? tp.GetString() : "stream";
                    if (type != "stream") continue;

                    var url = Str(el, "url");
                    var idx = url.IndexOf("?v=", StringComparison.Ordinal);
                    var videoId = idx >= 0 ? url[(idx + 3)..] : "";
                    var title = Str(el, "title");
                    if (videoId.Length == 0 || title.Length == 0) continue;

                    var duration = el.TryGetProperty("duration", out var dp) && dp.ValueKind == JsonValueKind.Number
                        ? (int)Math.Clamp(dp.GetDouble(), -1, int.MaxValue) : 0;

                    // Si sale en varias búsquedas, cuenta su mejor posición.
                    if (pool.TryGetValue(videoId, out var existing))
                    {
                        if (position < existing.Candidate.BestPosition)
                            pool[videoId] = existing with { Candidate = existing.Candidate with { BestPosition = position } };
                    }
                    else
                    {
                        pool[videoId] = new PoolItem(el.Clone(),
                            new GenreCandidate(videoId, title, Str(el, "uploaderName"), duration, position));
                    }
                    position++;
                }
            }
            catch { /* respuesta que no es el JSON esperado: se ignora esa búsqueda */ }
        }
    }

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() ?? "" : "";
}
