using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Threading.Tasks;
using eMusicApp.Models;

namespace eMusicApp.Services
{
    public class ApiService
    {
        private readonly HttpClient _httpClient;

        // eMusicApi corriendo en la Raspberry Pi 5
        // Puerto 5050 abierto en el router → Pi en 192.168.1.36
        private const string BaseUrl = "http://emusicmp3.duckdns.org:5050/api";

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public ApiService()
        {
            _httpClient = new HttpClient
            {
                Timeout = System.TimeSpan.FromSeconds(20)
            };
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "eMusicApp/1.0");
        }

        // ─────────────────────────────────────────────
        // BUSQUEDA — Solo se dispara al presionar Buscar/Enter
        // La API tiene caché de 30 minutos (IMemoryCache en la Pi)
        // ─────────────────────────────────────────────
        public async Task<List<Track>> SearchTracksAsync(string query)
        {
            try
            {
                var url = $"{BaseUrl}/search?q={Uri.EscapeDataString(query)}";
                System.Diagnostics.Debug.WriteLine($"[API] Buscando: {url}");

                var response = await _httpClient.GetAsync(url);
                
                string json;
                if (response.IsSuccessStatusCode)
                {
                    json = await response.Content.ReadAsStringAsync();
                }
                else
                {
                    // Fallback to public Piped API instance if Raspberry Pi backend fails or is rate-limited!
                    System.Diagnostics.Debug.WriteLine($"[API] Pi Backend failed ({(int)response.StatusCode}). Trying public Piped fallback...");
                    var fallbackUrl = $"https://pipedapi.kavin.rocks/search?q={Uri.EscapeDataString(query)}&filter=music_songs";
                    using var fallbackClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    fallbackClient.DefaultRequestHeaders.Add("User-Agent", "eMusicApp/1.0");
                    var fallbackResponse = await fallbackClient.GetAsync(fallbackUrl);
                    fallbackResponse.EnsureSuccessStatusCode();
                    json = await fallbackResponse.Content.ReadAsStringAsync();
                }

                using var document = JsonDocument.Parse(json);
                var itemsElement = document.RootElement.GetProperty("items");

                var allItems = JsonSerializer.Deserialize<List<Track>>(itemsElement.GetRawText(), JsonOpts)
                               ?? new List<Track>();

                // Tolerance to missing type or casing differences
                var tracks = allItems.FindAll(t => 
                    (string.IsNullOrEmpty(t.Type) || t.Type.Equals("stream", StringComparison.OrdinalIgnoreCase)) 
                    && !string.IsNullOrEmpty(t.Title));

                System.Diagnostics.Debug.WriteLine($"[API] Tracks encontradas: {tracks.Count}");
                return tracks;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] SearchTracksAsync ERROR: {ex.Message}. Trying backup public Piped...");
                try
                {
                    var fallbackUrl = $"https://api.piped.yt/search?q={Uri.EscapeDataString(query)}&filter=music_songs";
                    using var fallbackClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                    fallbackClient.DefaultRequestHeaders.Add("User-Agent", "eMusicApp/1.0");
                    var fallbackResponse = await fallbackClient.GetAsync(fallbackUrl);
                    if (fallbackResponse.IsSuccessStatusCode)
                    {
                        var json = await fallbackResponse.Content.ReadAsStringAsync();
                        using var document = JsonDocument.Parse(json);
                        var itemsElement = document.RootElement.GetProperty("items");
                        var allItems = JsonSerializer.Deserialize<List<Track>>(itemsElement.GetRawText(), JsonOpts)
                                       ?? new List<Track>();
                        return allItems.FindAll(t => 
                            (string.IsNullOrEmpty(t.Type) || t.Type.Equals("stream", StringComparison.OrdinalIgnoreCase)) 
                            && !string.IsNullOrEmpty(t.Title));
                    }
                }
                catch { }

                return new List<Track>();
            }
        }

        // ─────────────────────────────────────────────
        // STREAM — URL de audio para reproducir
        // La API tiene caché de 60 minutos
        // ─────────────────────────────────────────────
        public async Task<StreamInfo?> GetStreamAsync(string videoId)
        {
            try
            {
                var url = $"{BaseUrl}/streams/{Uri.EscapeDataString(videoId)}";
                System.Diagnostics.Debug.WriteLine($"[API] Stream: {url}");

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<StreamInfo>(json, JsonOpts);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] GetStreamAsync ERROR: {ex.Message}");
                return null;
            }
        }

        // ─────────────────────────────────────────────
        // TRENDING — Con caché de 1 hora en la Pi
        // ─────────────────────────────────────────────
        public async Task<List<Track>> GetTrendingAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{BaseUrl}/trending");
                if (!response.IsSuccessStatusCode) return new List<Track>();

                var json = await response.Content.ReadAsStringAsync();
                using var document = JsonDocument.Parse(json);
                var itemsElement = document.RootElement.GetProperty("items");

                var items = JsonSerializer.Deserialize<List<Track>>(itemsElement.GetRawText(), JsonOpts)
                            ?? new List<Track>();
                return items.FindAll(t => !string.IsNullOrEmpty(t.Title));
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] GetTrendingAsync ERROR: {ex.Message}");
                return new List<Track>();
            }
        }

        // ─────────────────────────────────────────────
        // FAVORITES — Guardados en SQLite de la Pi
        // ─────────────────────────────────────────────
        public async Task<List<Track>> GetFavoritesAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{BaseUrl}/favorites");
                if (!response.IsSuccessStatusCode) return new List<Track>();
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<Track>>(json, JsonOpts) ?? new List<Track>();
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] GetFavoritesAsync ERROR: {ex.Message}");
                return new List<Track>();
            }
        }

        public async Task AddFavoriteAsync(Track track)
        {
            try
            {
                // Mapear campos de Track al modelo del API (Favorite)
                var payload = new
                {
                    title = track.Title,
                    artist = track.Uploader,
                    thumbnailUrl = track.ThumbnailUrl,
                    duration = track.Duration,
                    videoId = track.VideoId
                };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                await _httpClient.PostAsync($"{BaseUrl}/favorites", content);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] AddFavoriteAsync ERROR: {ex.Message}");
            }
        }

        public async Task RemoveFavoriteAsync(string id)
        {
            try
            {
                await _httpClient.DeleteAsync($"{BaseUrl}/favorites/{id}");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] RemoveFavoriteAsync ERROR: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────
        // HISTORY — Guardado en SQLite de la Pi
        // ─────────────────────────────────────────────
        public async Task<List<Track>> GetHistoryAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{BaseUrl}/history");
                if (!response.IsSuccessStatusCode) return new List<Track>();
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<Track>>(json, JsonOpts) ?? new List<Track>();
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] GetHistoryAsync ERROR: {ex.Message}");
                return new List<Track>();
            }
        }

        public async Task AddHistoryAsync(Track track)
        {
            try
            {
                var payload = new
                {
                    title = track.Title,
                    artist = track.Uploader,
                    thumbnailUrl = track.ThumbnailUrl,
                    duration = track.Duration,
                    videoId = track.VideoId
                };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                await _httpClient.PostAsync($"{BaseUrl}/history", content);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] AddHistoryAsync ERROR: {ex.Message}");
            }
        }

        // ─────────────────────────────────────────────
        // PLAYLISTS — Guardadas en SQLite de la Pi
        // ─────────────────────────────────────────────
        public async Task<List<Playlist>> GetPlaylistsAsync()
        {
            try
            {
                var response = await _httpClient.GetAsync($"{BaseUrl}/playlists");
                if (!response.IsSuccessStatusCode) return new List<Playlist>();
                var json = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<List<Playlist>>(json, JsonOpts) ?? new List<Playlist>();
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] GetPlaylistsAsync ERROR: {ex.Message}");
                return new List<Playlist>();
            }
        }
    }

    // ─────────────────────────────────────────────
    // Modelos de respuesta del API de streams
    // ─────────────────────────────────────────────
    public class StreamInfo
    {
        [JsonPropertyName("title")]
        public string Title { get; set; }

        [JsonPropertyName("uploader")]
        public string Uploader { get; set; }

        [JsonPropertyName("thumbnailUrl")]
        public string ThumbnailUrl { get; set; }

        [JsonPropertyName("audioStreams")]
        public List<AudioStream> AudioStreams { get; set; }

        [JsonPropertyName("relatedStreams")]
        public List<Track> RelatedStreams { get; set; }
    }

    public class AudioStream
    {
        [JsonPropertyName("url")]
        public string Url { get; set; }

        [JsonPropertyName("bitrate")]
        public int Bitrate { get; set; }

        [JsonPropertyName("quality")]
        public string Quality { get; set; }

        [JsonPropertyName("mimeType")]
        public string MimeType { get; set; }
    }
}
