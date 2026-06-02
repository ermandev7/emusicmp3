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

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        return Ok(await _context.Favorites.AsNoTracking().ToListAsync());
    }

    [HttpPost]
    public async Task<IActionResult> Post([FromBody] FavoriteRequest req)
    {
        var id = req.VideoId ?? req.Id ?? string.Empty;
        if (string.IsNullOrEmpty(id)) return BadRequest("videoId is required.");

        if (await _context.Favorites.AnyAsync(f => f.Id == id))
            return Conflict("Already exists in favorites.");

        var favorite = new Favorite
        {
            Id           = id,
            Title        = req.Title ?? string.Empty,
            Artist       = req.Artist ?? string.Empty,
            ThumbnailUrl = req.ThumbnailUrl ?? string.Empty,
            Duration     = req.Duration
        };

        _context.Favorites.Add(favorite);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = favorite.Id }, favorite);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id)
    {
        var fav = await _context.Favorites.FindAsync(id);
        if (fav == null) return NotFound();

        _context.Favorites.Remove(fav);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}

// DTO para aceptar el payload del POST desde la app MAUI
public class FavoriteRequest
{
    public string? Id           { get; set; }
    public string? VideoId      { get; set; } // Alias de Id
    public string? Title        { get; set; }
    public string? Artist       { get; set; }
    public string? ThumbnailUrl { get; set; }
    public long    Duration     { get; set; }
}
