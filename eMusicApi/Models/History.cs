using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace eMusicApi.Models;

public class History
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    public string VideoId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Artist { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public long Duration { get; set; }
    public int PlayCount { get; set; } = 1;
    public DateTime PlayedAt { get; set; } = DateTime.UtcNow;
    public string UserId { get; set; } = "";
}
