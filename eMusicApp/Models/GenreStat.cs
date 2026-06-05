using System.Text.Json.Serialization;

namespace eMusicApp.Models
{
    public class GenreStat
    {
        [JsonPropertyName("genre")]
        public string Genre { get; set; } = "";

        [JsonPropertyName("playCount")]
        public int PlayCount { get; set; }
    }
}
