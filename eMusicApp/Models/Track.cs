using System.Text.Json.Serialization;

namespace eMusicApp.Models
{
    public class Track
    {
        // Tipo de item: "stream" = canción/video, "channel" = canal, "playlist" = lista
        [JsonPropertyName("type")]
        public string Type { get; set; }

        // URL relativa del video, ej: /watch?v=hLQl3WQQoQ0
        [JsonPropertyName("url")]
        public string Url { get; set; }

        // Para uso interno (ID de la base de datos local)
        public string Id { get; set; }

        [JsonPropertyName("title")]
        public string Title { get; set; }

        // Piped usa "uploaderName" en búsqueda y "uploader" en streams
        [JsonPropertyName("uploaderName")]
        public string Uploader { get; set; }

        [JsonPropertyName("thumbnail")]
        public string ThumbnailUrl { get; set; }

        [JsonPropertyName("duration")]
        public int Duration { get; set; }

        // Extrae el videoId de la URL relativa (/watch?v=XXXX)
        public string VideoId
        {
            get
            {
                if (string.IsNullOrEmpty(Url)) return string.Empty;
                var idx = Url.IndexOf("?v=");
                return idx >= 0 ? Url.Substring(idx + 3) : Url.TrimStart('/');
            }
        }
    }
}
