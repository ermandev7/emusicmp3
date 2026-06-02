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
}
