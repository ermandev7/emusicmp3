using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using eMusicApi.Services;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class StreamsController : ControllerBase
{
    private readonly PipedApiService _apiService;

    public StreamsController(PipedApiService apiService)
    {
        _apiService = apiService;
    }

    [HttpGet("{videoId}")]
    public async Task<IActionResult> Get(string videoId)
    {
        try
        {
            var result = await _apiService.GetStreamAsync(videoId);
            return Content(result, "application/json");
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
}
