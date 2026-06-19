using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace eMusicApi.Models;

/// <summary>
/// Canción o artista que el usuario marcó como "no recomendar".
/// Se filtra al generar recomendaciones.
/// </summary>
public class Exclusion
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public string VideoId { get; set; } = "";
    public string Artist { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
