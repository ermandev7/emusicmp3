using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using eMusicApi.Data;
using eMusicApi.Models;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HistoryController : ControllerBase
{
    private readonly AppDbContext _context;

    public HistoryController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        return Ok(await _context.History.AsNoTracking()
            .OrderByDescending(h => h.PlayedAt).Take(50).ToListAsync());
    }

    [HttpPost]
    public async Task<IActionResult> Post(History history)
    {
        // Upsert: si ya existe, incrementar PlayCount y actualizar fecha
        var existing = await _context.History
            .FirstOrDefaultAsync(h => h.VideoId == history.VideoId);

        if (existing != null)
        {
            existing.PlayCount++;
            existing.PlayedAt = System.DateTime.UtcNow;
            existing.Title = history.Title;
            existing.Artist = history.Artist;
            if (!string.IsNullOrEmpty(history.ThumbnailUrl))
                existing.ThumbnailUrl = history.ThumbnailUrl;
        }
        else
        {
            history.PlayCount = 1;
            history.PlayedAt = System.DateTime.UtcNow;
            _context.History.Add(history);
        }

        await _context.SaveChangesAsync();

        // Limpiar historial antiguo (mantener los 200 más recientes)
        var oldHistory = await _context.History
            .OrderByDescending(h => h.PlayedAt).Skip(200).ToListAsync();
        if (oldHistory.Any())
        {
            _context.History.RemoveRange(oldHistory);
            await _context.SaveChangesAsync();
        }

        return CreatedAtAction(nameof(Get), new { id = existing?.Id ?? history.Id }, existing ?? history);
    }

    /// <summary>
    /// Devuelve los géneros más escuchados, ordenados por total de reproducciones.
    /// Analiza título y artista de todo el historial con detección de género por keywords.
    /// </summary>
    [HttpGet("top-genres")]
    public async Task<IActionResult> GetTopGenres()
    {
        var all = await _context.History.AsNoTracking().ToListAsync();
        var counts = new Dictionary<string, int>();

        foreach (var h in all)
        {
            var genre = DetectGenre(h.Title, h.Artist);
            if (genre != null)
                counts[genre] = counts.TryGetValue(genre, out var c) ? c + h.PlayCount : h.PlayCount;
        }

        var sorted = counts.OrderByDescending(kv => kv.Value)
            .Select(kv => new { genre = kv.Key, playCount = kv.Value })
            .ToList();

        return Ok(sorted);
    }

    private static string? DetectGenre(string title, string artist)
    {
        var combined = $"{title} {artist}".ToLowerInvariant();

        // Géneros directos
        string[] genres = { "salsa", "bachata", "reggaeton", "cumbia", "merengue",
            "vallenato", "rock", "pop", "rap", "trap", "balada", "ranchera",
            "corrido", "electronic", "jazz", "blues", "reggae", "clasica", "kpop", "r&b" };

        foreach (var g in genres)
            if (combined.Contains(g)) return g;

        // Heurísticas
        if (combined.Contains("reggaet") || combined.Contains("perreo")) return "reggaeton";
        if (combined.Contains("cumbi")) return "cumbia";
        if (combined.Contains("bachi")) return "bachata";
        if (combined.Contains("ranchera") || combined.Contains("mariachi") || combined.Contains("norteñ")) return "ranchera";
        if (combined.Contains("corrido") || combined.Contains("tumbado")) return "corrido";
        if (combined.Contains("romántic") || combined.Contains("amor") || combined.Contains("corazón")) return "balada";

        return null;
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var history = await _context.History.FindAsync(id);
        if (history == null) return NotFound();

        _context.History.Remove(history);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete]
    public async Task<IActionResult> Clear()
    {
        _context.History.RemoveRange(_context.History);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
