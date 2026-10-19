namespace eMusicApi.Models;

public class Favorite
{
    // Clave primaria compuesta (Id, UserId), definida en AppDbContext.OnModelCreating:
    // cada usuario tiene sus propios favoritos, aunque dos coincidan en la misma canción.
    public string Id { get; set; } // videoId
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public long Duration { get; set; }
    public string UserId { get; set; } = "";
}
