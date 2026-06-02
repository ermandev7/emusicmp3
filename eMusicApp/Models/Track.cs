using System.Text.Json.Serialization;

namespace eMusicApp.Models
{
    public class Track
    {
        // Tipo de item: "stream" = canción/video, "channel" = canal, "playlist" = lista
        [JsonPropertyName("type")]
        public string Type { get; set; } = "stream";

        // JSON properties fallback
        [JsonPropertyName("videoId")]
        public string? VideoIdFromJson { get; set; }

        [JsonPropertyName("artist")]
        public string? ArtistFromJson { get; set; }

        [JsonPropertyName("uploader")]
        public string? UploaderFromJson { get; set; }

        // Invidious usa "author" en lugar de "uploaderName"
        [JsonPropertyName("author")]
        public string? AuthorFromJson { get; set; }

        [JsonPropertyName("thumbnailUrl")]
        public string? ThumbnailUrlFromJson { get; set; }

        private string _url = "";
        // URL relativa del video, ej: /watch?v=hLQl3WQQoQ0
        [JsonPropertyName("url")]
        public string Url 
        { 
            get => !string.IsNullOrEmpty(_url) ? _url : (!string.IsNullOrEmpty(VideoId) ? $"/watch?v={VideoId}" : string.Empty);
            set => _url = value;
        }

        // Para uso interno (ID de la base de datos local)
        public string Id { get; set; } = "";

        [JsonPropertyName("title")]
        public string Title { get; set; } = "";

        private string _uploader = "";
        // Piped usa "uploaderName", la BD usa "artist", Invidious usa "author"
        [JsonPropertyName("uploaderName")]
        public string Uploader 
        { 
            get => !string.IsNullOrEmpty(_uploader) ? _uploader 
                 : (!string.IsNullOrEmpty(ArtistFromJson)   ? ArtistFromJson 
                 : (!string.IsNullOrEmpty(UploaderFromJson) ? UploaderFromJson 
                 : (!string.IsNullOrEmpty(AuthorFromJson)   ? AuthorFromJson 
                 : string.Empty)));
            set => _uploader = value;
        }

        private string _thumbnailUrl = "";
        [JsonPropertyName("thumbnail")]
        public string ThumbnailUrl 
        { 
            get => !string.IsNullOrEmpty(_thumbnailUrl) ? _thumbnailUrl : (!string.IsNullOrEmpty(ThumbnailUrlFromJson) ? ThumbnailUrlFromJson : string.Empty);
            set => _thumbnailUrl = value;
        }

        [JsonPropertyName("duration")]
        public int Duration { get; set; }

        // Extrae el videoId de la URL relativa (/watch?v=XXXX)
        [JsonIgnore]
        public string VideoId
        {
            get
            {
                if (!string.IsNullOrEmpty(VideoIdFromJson)) return VideoIdFromJson;
                if (string.IsNullOrEmpty(Url)) return string.Empty;
                var idx = Url.IndexOf("?v=");
                return idx >= 0 ? Url.Substring(idx + 3) : Url.TrimStart('/');
            }
        }
    }
}
