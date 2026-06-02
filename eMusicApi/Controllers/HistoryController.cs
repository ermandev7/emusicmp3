using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Threading.Tasks;
using eMusicApi.Data;
using eMusicApi.Models;

namespace eMusicApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class HistoryController : ControllerBase
{
    private readonly AppDbContext _context;

    public HistoryController(AppDbContext context)
    {
        _context = context;
    }

    [HttpGet]
    public async Task<IActionResult> Get()
    {
        return Ok(await _context.History.AsNoTracking().OrderByDescending(h => h.PlayedAt).ToListAsync());
    }

    [HttpPost]
    public async Task<IActionResult> Post(History history)
    {
        history.PlayedAt = System.DateTime.UtcNow;
        _context.History.Add(history);
        await _context.SaveChangesAsync();
        return CreatedAtAction(nameof(Get), new { id = history.Id }, history);
    }

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(int id)
    {
        var history = await _context.History.FindAsync(id);
        if (history == null) return NotFound();

        _context.History.Remove(history);
        await _context.SaveChangesAsync();
        return NoContent();
    }
    
    [HttpDelete]
    public async Task<IActionResult> Clear()
    {
        _context.History.RemoveRange(_context.History);
        await _context.SaveChangesAsync();
        return NoContent();
    }
}
