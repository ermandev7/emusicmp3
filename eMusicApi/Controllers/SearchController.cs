using Microsoft.AspNetCore.Mvc;
using System.Threading.Tasks;
using eMusicApi.Services;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SearchController : ControllerBase
{
    private readonly MusicExtractionService _musicService;
    private readonly GenreRadioService _genreRadio;

    public SearchController(MusicExtractionService musicService, GenreRadioService genreRadio)
    {
        _musicService = musicService;
        _genreRadio = genreRadio;
    }

    [HttpGet]
    public async Task<IActionResult> Get([FromQuery] string q)
    {
        if (string.IsNullOrEmpty(q)) return BadRequest("Query parameter 'q' is required.");

        // Botones de género de la pantalla de inicio: la app manda una búsqueda fija y
        // reproduce el primer resultado. Si el texto es exactamente el de uno de esos
        // botones, se responde con mixes de ese género ordenados para este usuario, en
        // el mismo formato que una búsqueda. Si algo falla, se sigue con la búsqueda
        // normal: el botón nunca se queda sin sonar por esto.
        if (GenreStations.TryMatch(q, out var station))
        {
            try
            {
                var userId = Request.Headers.TryGetValue("X-User-Id", out var val) ? val.ToString() : "";
                var personalized = await _genreRadio.BuildAsync(userId, station);
                if (personalized != null)
                    return Content(personalized, "application/json");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Género] {station.Key}: fallo ({ex.Message}); se usa la búsqueda normal.");
            }
        }

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
