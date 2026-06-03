using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace eMusicApp.Models
{
    public class Playlist
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string? Description { get; set; }
        public string SongsJson { get; set; } = "[]";

        [JsonIgnore]
        public List<Track> Tracks
        {
            get
            {
                try
                {
                    return JsonSerializer.Deserialize<List<Track>>(SongsJson ?? "[]",
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                        ?? new List<Track>();
                }
                catch { return new List<Track>(); }
            }
        }

        [JsonIgnore]
        public int TrackCount => Tracks.Count;

        [JsonIgnore]
        public string? CoverUrl => Tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.ThumbnailUrl))?.ThumbnailUrl;

        [JsonIgnore]
        public string TrackCountText => TrackCount == 1 ? "1 canción" : $"{TrackCount} canciones";

        [JsonIgnore]
        public bool HasCover => !string.IsNullOrEmpty(CoverUrl);

        [JsonIgnore]
        public bool HasNoCover => string.IsNullOrEmpty(CoverUrl);
    }
}
