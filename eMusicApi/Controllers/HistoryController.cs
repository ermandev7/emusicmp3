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

    private string GetUserId() =>
        Request.Headers.TryGetValue("X-User-Id", out var val) ? val.ToString() : "";

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userId = GetUserId();
        return Ok(await _context.History.AsNoTracking()
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.PlayedAt).Take(50).ToListAsync());
    }

    [HttpPost]
    public async Task<IActionResult> Post(History history)
    {
        var userId = GetUserId();
        history.UserId = userId;

        // Upsert: si ya existe para este usuario, incrementar PlayCount
        var existing = await _context.History
            .FirstOrDefaultAsync(h => h.VideoId == history.VideoId && h.UserId == userId);

        if (existing != null)
        {
            existing.PlayCount++;
            existing.PlayedAt = System.DateTime.UtcNow;
            existing.Title = history.Title;
            existing.Artist = history.Artist;
            existing.IsDownloaded = history.IsDownloaded;
            existing.SkippedEarly = false;
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

        // Limpiar historial antiguo de este usuario (mantener 200)
        var oldHistory = await _context.History
            .Where(h => h.UserId == userId)
            .OrderByDescending(h => h.PlayedAt).Skip(200).ToListAsync();
        if (oldHistory.Any())
        {
            _context.History.RemoveRange(oldHistory);
            await _context.SaveChangesAsync();
        }

        return CreatedAtAction(nameof(Get), new { id = existing?.Id ?? history.Id }, existing ?? history);
    }

    [HttpGet("top-genres")]
    public async Task<IActionResult> GetTopGenres()
    {
        var userId = GetUserId();
        var all = await _context.History.AsNoTracking()
            .Where(h => h.UserId == userId)
            .ToListAsync();
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

        string[] genres = { "salsa", "bachata", "reggaeton", "cumbia", "merengue",
            "vallenato", "rock", "pop", "rap", "trap", "balada", "ranchera",
            "corrido", "electronic", "jazz", "blues", "reggae", "clasica", "kpop", "r&b" };

        foreach (var g in genres)
            if (combined.Contains(g)) return g;

        if (combined.Contains("reggaet") || combined.Contains("perreo")) return "reggaeton";
        if (combined.Contains("cumbi")) return "cumbia";
        if (combined.Contains("bachi")) return "bachata";
        if (combined.Contains("ranchera") || combined.Contains("mariachi") || combined.Contains("norteñ")) return "ranchera";
        if (combined.Contains("corrido") || combined.Contains("tumbado")) return "corrido";
        if (combined.Contains("romántic") || combined.Contains("amor") || combined.Contains("corazón")) return "balada";

        return null;
    }

    [HttpPatch("{videoId}/skip")]
    public async Task<IActionResult> MarkSkipped(string videoId)
    {
        var userId = GetUserId();
        var entry = await _context.History
            .FirstOrDefaultAsync(h => h.VideoId == videoId && h.UserId == userId);
        if (entry == null) return NotFound();

        entry.SkippedEarly = true;
        await _context.SaveChangesAsync();
        return NoContent();
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
        var userId = GetUserId();
        var userHistory = await _context.History.Where(h => h.UserId == userId).ToListAsync();
        _context.History.RemoveRange(userHistory);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
