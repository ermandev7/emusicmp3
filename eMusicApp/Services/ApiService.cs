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
        private const string BaseUrl = AppConstants.ApiBaseUrl;

        private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        public ApiService()
        {
            _httpClient = new HttpClient
            {
                // 25s: suficiente para yt-dlp (~15-20s) sin bloquear la UI demasiado
                Timeout = System.TimeSpan.FromSeconds(25)
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
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    var tracks = ParseSearchJson(json);
                    if (tracks.Count > 0) return tracks;
                }
                System.Diagnostics.Debug.WriteLine($"[API] Pi Backend returned non-success or empty: {response.StatusCode}. Trying fallbacks...");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] Pi Backend exception: {ex.Message}. Trying fallbacks...");
            }

            // Sequential fallbacks — reutilizamos _httpClient para evitar socket exhaustion
            var fallbacks = new[]
            {
                $"https://pipedapi.kavin.rocks/search?q={Uri.EscapeDataString(query)}&filter=music_songs",
                $"https://pipedapi.colby.land/search?q={Uri.EscapeDataString(query)}&filter=music_songs",
                $"https://piped-api.garudalinux.org/search?q={Uri.EscapeDataString(query)}&filter=music_songs",
                $"https://api.piped.yt/search?q={Uri.EscapeDataString(query)}&filter=music_songs"
            };

            foreach (var fallbackUrl in fallbacks)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[API] Trying fallback: {fallbackUrl}");
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(7));
                    var response = await _httpClient.GetAsync(fallbackUrl, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var tracks = ParseSearchJson(json);
                        if (tracks.Count > 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[API] Fallback succeeded! Found {tracks.Count} tracks.");
                            return tracks;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] Fallback failed: {ex.Message}");
                }
            }

            return new List<Track>();
        }

        private List<Track> ParseSearchJson(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                var itemsElement = document.RootElement.GetProperty("items");
                var allItems = JsonSerializer.Deserialize<List<Track>>(itemsElement.GetRawText(), JsonOpts)
                               ?? new List<Track>();

                return allItems.FindAll(t => 
                    (string.IsNullOrEmpty(t.Type) || t.Type.Equals("stream", StringComparison.OrdinalIgnoreCase)) 
                    && !string.IsNullOrEmpty(t.Title));
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] ParseSearchJson error: {ex.Message}");
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
                if (response.IsSuccessStatusCode)
                {
                    var json = await response.Content.ReadAsStringAsync();
                    return JsonSerializer.Deserialize<StreamInfo>(json, JsonOpts);
                }
                System.Diagnostics.Debug.WriteLine($"[API] Pi Backend stream returned non-success. Trying fallbacks...");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] GetStreamAsync Pi Backend ERROR: {ex.Message}. Trying fallbacks...");
            }

            // Fallback sequential streams — reutilizamos _httpClient
            var fallbacks = new[]
            {
                $"https://pipedapi.kavin.rocks/streams/{Uri.EscapeDataString(videoId)}",
                $"https://pipedapi.colby.land/streams/{Uri.EscapeDataString(videoId)}",
                $"https://piped-api.garudalinux.org/streams/{Uri.EscapeDataString(videoId)}",
                $"https://api.piped.yt/streams/{Uri.EscapeDataString(videoId)}"
            };

            foreach (var fallbackUrl in fallbacks)
            {
                try
                {
                    System.Diagnostics.Debug.WriteLine($"[API] Trying stream fallback: {fallbackUrl}");
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(7));
                    var response = await _httpClient.GetAsync(fallbackUrl, cts.Token);
                    if (response.IsSuccessStatusCode)
                    {
                        var json = await response.Content.ReadAsStringAsync();
                        var info = JsonSerializer.Deserialize<StreamInfo>(json, JsonOpts);
                        if (info != null && info.AudioStreams != null && info.AudioStreams.Count > 0)
                        {
                            System.Diagnostics.Debug.WriteLine($"[API] Stream fallback succeeded!");
                            return info;
                        }
                    }
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[API] Stream fallback failed: {ex.Message}");
                }
            }

            return null;
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

        public async Task<bool> IsFavoriteAsync(string videoId)
        {
            try
            {
                var response = await _httpClient.GetAsync($"{BaseUrl}/favorites/{Uri.EscapeDataString(videoId)}");
                return response.IsSuccessStatusCode;
            }
            catch (System.Exception)
            {
                return false;
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

        public async Task<Playlist?> CreatePlaylistAsync(string name)
        {
            try
            {
                var payload = new { name };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                var response = await _httpClient.PostAsync($"{BaseUrl}/playlists", content);
                if (!response.IsSuccessStatusCode) return null;
                var resJson = await response.Content.ReadAsStringAsync();
                return JsonSerializer.Deserialize<Playlist>(resJson, JsonOpts);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] CreatePlaylistAsync ERROR: {ex.Message}");
                return null;
            }
        }

        public async Task<bool> DeletePlaylistAsync(int id)
        {
            try
            {
                var response = await _httpClient.DeleteAsync($"{BaseUrl}/playlists/{id}");
                return response.IsSuccessStatusCode;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] DeletePlaylistAsync ERROR: {ex.Message}");
                return false;
            }
        }

        public async Task AddTrackToPlaylistAsync(int playlistId, Track track)
        {
            try
            {
                var payload = new
                {
                    videoId = track.VideoId,
                    title = track.Title,
                    uploaderName = track.Uploader,
                    thumbnail = track.ThumbnailUrl,
                    duration = track.Duration,
                    url = track.Url,
                    type = "stream"
                };
                var json = JsonSerializer.Serialize(payload);
                var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
                await _httpClient.PostAsync($"{BaseUrl}/playlists/{playlistId}/tracks", content);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] AddTrackToPlaylistAsync ERROR: {ex.Message}");
            }
        }

        public async Task RemoveTrackFromPlaylistAsync(int playlistId, string videoId)
        {
            try
            {
                await _httpClient.DeleteAsync($"{BaseUrl}/playlists/{playlistId}/tracks/{Uri.EscapeDataString(videoId)}");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] RemoveTrackFromPlaylistAsync ERROR: {ex.Message}");
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
