using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using eMusicApi.Services;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly PipedApiService _apiService;

    public SearchController(PipedApiService apiService)
    {
        _apiService = apiService;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string q)
    {
        if (string.IsNullOrEmpty(q)) return BadRequest("Query parameter 'q' is required.");
        
        try
        {
            var result = await _apiService.SearchAsync(q);
            return Content(result, "application/json");
        }
        catch (System.Exception ex)
        {
            return StatusCode(500, ex.Message);
        }
    }
}
