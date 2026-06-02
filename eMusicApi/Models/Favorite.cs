using System.ComponentModel.DataAnnotations;

namespace eMusicApi.Models;

public class Favorite
{
    [Key]
    public string Id { get; set; } // videoId
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public long Duration { get; set; }
}
