using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using eMusicApi.Data;
using eMusicApi.Models;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class FavoritesController : ControllerBase
{
    private readonly AppDbContext _context;

    public FavoritesController(AppDbContext context)
    {
        _context = context;
    }

    private string GetUserId() =>
        Request.Headers.TryGetValue("X-User-Id", out var val) ? val.ToString() : "";

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        var userId = GetUserId();
        return Ok(await _context.Favorites.AsNoTracking()
            .Where(f => f.UserId == userId).ToListAsync());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> GetById(string id)
    {
        var userId = GetUserId();
        var exists = await _context.Favorites.AnyAsync(f => f.Id == id && f.UserId == userId);
        return exists ? Ok() : NotFound();
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] FavoriteRequest req)
    {
        var userId = GetUserId();
        var id = req.VideoId ?? req.Id ?? string.Empty;
        if (string.IsNullOrEmpty(id)) return BadRequest("videoId is required.");

        if (await _context.Favorites.AnyAsync(f => f.Id == id && f.UserId == userId))
            return Conflict("Already exists in favorites.");

        var favorite = new Favorite
        {
            Id           = id,
            Title        = req.Title ?? string.Empty,
            Artist       = req.Artist ?? string.Empty,
            ThumbnailUrl = req.ThumbnailUrl ?? string.Empty,
            Duration     = req.Duration,
            UserId       = userId
        };

        _context.Favorites.Add(favorite);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = favorite.Id }, favorite);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var userId = GetUserId();
        var fav = await _context.Favorites.FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId);
        if (fav == null) return NotFound();

        _context.Favorites.Remove(fav);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}

public class FavoriteRequest
{
    public string? Id           { get; set; }
    public string? VideoId      { get; set; }
    public string? Title        { get; set; }
    public string? Artist       { get; set; }
    public string? ThumbnailUrl { get; set; }
    public long    Duration     { get; set; }
}
