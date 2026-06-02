using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Threading.Tasks;
using eMusicApi.Services;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TrendingController : ControllerBase
{
    private readonly MusicExtractionService _musicService;

    public TrendingController(MusicExtractionService musicService)
    {
        _musicService = musicService;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            // Piped devuelve un array plano [...]
            // Lo envolvemos en {"items": [...]} para que sea consistente con /search
            var raw = await _musicService.GetTrendingAsync();
            
            // Puede venir como array o ya con items
            if (raw.TrimStart().StartsWith("["))
            {
                var array = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonArray>(raw);
                var wrapped = new { items = array };
                return Ok(wrapped);
            }
            return Content(raw, "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
