using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Threading.Tasks;
using System.Text.Json.Nodes;
using eMusicApi.Data;
using eMusicApi.Models;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PlaylistsController : ControllerBase
{
    private readonly AppDbContext _context;

    public PlaylistsController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        return Ok(await _context.Playlists.AsNoTracking().ToListAsync());
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(int id)
    {
        var playlist = await _context.Playlists.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id);
        if (playlist == null) return NotFound();
        return Ok(playlist);
    }

    [HttpPost]
    public async Task<IActionResult> Post(Playlist playlist)
    {
        _context.Playlists.Add(playlist);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = playlist.Id }, playlist);
    }

    [HttpPut("{id}")]
    public async Task<IActionResult> Put(int id, Playlist updatedPlaylist)
    {
        if (id != updatedPlaylist.Id) return BadRequest();

        var existing = await _context.Playlists.FindAsync(id);
        if (existing == null) return NotFound();

        existing.Name = updatedPlaylist.Name;
        existing.Description = updatedPlaylist.Description;
        existing.SongsJson = updatedPlaylist.SongsJson;

        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var playlist = await _context.Playlists.FindAsync(id);
        if (playlist == null) return NotFound();

        _context.Playlists.Remove(playlist);
        await _context.SaveChangesAsync();
        return NoContent();
    }

    [HttpPost("{id}/tracks")]
    public async Task<IActionResult> AddTrack(int id, [FromBody] JsonNode? track)
    {
        if (track == null) return BadRequest();
        var playlist = await _context.Playlists.FindAsync(id);
        if (playlist == null) return NotFound();

        var array = JsonNode.Parse(playlist.SongsJson ?? "[]")?.AsArray() ?? new JsonArray();

        // Remove duplicate by videoId
        var videoId = track["videoId"]?.GetValue<string>();
        if (videoId != null)
        {
            for (int i = array.Count - 1; i >= 0; i--)
            {
                if (array[i]?["videoId"]?.GetValue<string>() == videoId)
                    array.RemoveAt(i);
            }
        }

        array.Insert(0, track.DeepClone());
        playlist.SongsJson = array.ToJsonString();
        await _context.SaveChangesAsync();
        return Ok();
    }

    [HttpDelete("{id}/tracks/{videoId}")]
    public async Task<IActionResult> RemoveTrack(int id, string videoId)
    {
        var playlist = await _context.Playlists.FindAsync(id);
        if (playlist == null) return NotFound();

        var array = JsonNode.Parse(playlist.SongsJson ?? "[]")?.AsArray() ?? new JsonArray();
        for (int i = array.Count - 1; i >= 0; i--)
        {
            if (array[i]?["videoId"]?.GetValue<string>() == videoId)
                array.RemoveAt(i);
        }
        playlist.SongsJson = array.ToJsonString();
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
