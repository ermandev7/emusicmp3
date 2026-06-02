using Microsoft.AspNetCore.Mvc;
using System.Text.Json;
using System.Threading.Tasks;
using eMusicApi.Services;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class TrendingController : ControllerBase
{
    private readonly PipedApiService _apiService;

    public TrendingController(PipedApiService apiService)
    {
        _apiService = apiService;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        try
        {
            // Piped devuelve un array plano [...]
            // Lo envolvemos en {"items": [...]} para que sea consistente con /search
            var raw = await _apiService.GetTrendingAsync();
            var array = JsonSerializer.Deserialize<System.Text.Json.Nodes.JsonArray>(raw);
            var wrapped = new { items = array };
            return Ok(wrapped);
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
}
