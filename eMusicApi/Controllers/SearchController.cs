using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using eMusicApi.Services;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly MusicExtractionService _musicService;

    public SearchController(MusicExtractionService musicService)
    {
        _musicService = musicService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string q)
    {
        if (string.IsNullOrEmpty(q)) return BadRequest("Query parameter 'q' is required.");

        try
        {
            var result = await _musicService.SearchAsync(q);
            return Content(result, "application/json");
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}
