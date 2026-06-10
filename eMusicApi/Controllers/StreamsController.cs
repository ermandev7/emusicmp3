using System.Linq;
using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using eMusicApi.Services;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StreamsController : ControllerBase
{
    private readonly MusicExtractionService _musicService;

    public StreamsController(MusicExtractionService musicService)
    {
        _musicService = musicService;
    }

    [HttpGet("{videoId}")]
    public async Task<IActionResult> Get(string videoId)
    {
        try
        {
            var result = await _musicService.GetStreamAsync(videoId);
            return Content(result, "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }

    /// <summary>
    /// Pre-fetch: calienta el cache de varios videoIds en background.
    /// La app llama esto al mostrar resultados de búsqueda para que
    /// cuando el usuario toque play, el stream ya esté en cache (~0ms).
    /// Responde 202 Accepted inmediatamente sin esperar.
    /// </summary>
    [HttpPost("prefetch")]
    public IActionResult Prefetch([FromBody] PrefetchRequest request)
    {
        if (request?.VideoIds == null || request.VideoIds.Length == 0)
            return BadRequest();

        // Limitar a 3 para no sobrecargar la Pi
        var ids = request.VideoIds.Take(3).ToArray();
        foreach (var id in ids)
            _ = _musicService.GetStreamAsync(id);

        return Accepted();
    }
}

public class PrefetchRequest
{
    public string[] VideoIds { get; set; } = Array.Empty<string>();
}
