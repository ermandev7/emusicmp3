using System.Collections.Generic;

namespace eMusicApi.Models;

public class Playlist
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public string? Description { get; set; }
    // En un caso real, esto sería una relación a una tabla PlaylistSongs.
    // Para mantenerlo simple, usaremos un JSON string o no añadiremos las canciones aquí aún.
    public string SongsJson { get; set; } = "[]";
    public string UserId { get; set; } = "";
}
