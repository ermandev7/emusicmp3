using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Generic;
using System.Threading.Tasks;
using eMusicApp.Models;
using Microsoft.Maui.Storage;

namespace eMusicApp.Services
{
    public class ApiService
    {
        private readonly HttpClient _httpClient;

        // URL del Piped Backend en la Raspberry Pi (via Caddy reverse proxy)
        private const string PipedApiUrl = "https://api.emusicmp3.duckdns.org";
        // URL del proxy de streams de audio
        private const string PipedProxyUrl = "https://proxy.emusicmp3.duckdns.org";

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
        // ─────────────────────────────────────────────
        public async Task<List<Track>> SearchTracksAsync(string query)
        {
            try
            {
                // filter=all devuelve resultados; music_songs devuelve vacío en esta instancia de Piped
                var url = $"{PipedApiUrl}/search?q={Uri.EscapeDataString(query)}&filter=all";
                System.Diagnostics.Debug.WriteLine($"[API] Buscando: {url}");

                var response = await _httpClient.GetAsync(url);
                response.EnsureSuccessStatusCode();

                var json = await response.Content.ReadAsStringAsync();
                System.Diagnostics.Debug.WriteLine($"[API] Respuesta: {json.Substring(0, Math.Min(200, json.Length))}...");

                using var document = JsonDocument.Parse(json);
                var itemsElement = document.RootElement.GetProperty("items");

                var allItems = JsonSerializer.Deserialize<List<Track>>(itemsElement.GetRawText(), JsonOpts) ?? new List<Track>();

                // Solo devolvemos streams (canciones/videos), no canales ni playlists
                var tracks = allItems.FindAll(t => t.Type == "stream" && !string.IsNullOrEmpty(t.Title));
                System.Diagnostics.Debug.WriteLine($"[API] Tracks encontradas: {tracks.Count}");
                return tracks;
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] SearchTracksAsync ERROR: {ex.Message}");
                return new List<Track>();
            }
        }

        // ─────────────────────────────────────────────
        // STREAM — Obtiene la URL de audio para reproducir
        // ─────────────────────────────────────────────
        public async Task<StreamInfo?> GetStreamAsync(string videoId)
        {
            try
            {
                var url = $"{PipedApiUrl}/streams/{Uri.EscapeDataString(videoId)}";
                System.Diagnostics.Debug.WriteLine($"[API] Obteniendo stream: {url}");

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
        // TRENDING — Canciones en tendencia
        // ─────────────────────────────────────────────
        public async Task<List<Track>> GetTrendingAsync()
        {
            try
            {
                var url = $"{PipedApiUrl}/trending?region=US";
                var response = await _httpClient.GetAsync(url);
                if (!response.IsSuccessStatusCode) return new List<Track>();

                var json = await response.Content.ReadAsStringAsync();
                var items = JsonSerializer.Deserialize<List<Track>>(json, JsonOpts) ?? new List<Track>();
                return items.FindAll(t => !string.IsNullOrEmpty(t.Title));
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[API] GetTrendingAsync ERROR: {ex.Message}");
                return new List<Track>();
            }
        }

        // ─────────────────────────────────────────────
        // FAVORITES — Almacenados localmente en el dispositivo
        // ─────────────────────────────────────────────
        private const string FavKey = "emusic_favorites";

        public async Task<List<Track>> GetFavoritesAsync()
        {
            var json = await SecureStorage.GetAsync(FavKey);
            if (string.IsNullOrEmpty(json)) return new List<Track>();
            return JsonSerializer.Deserialize<List<Track>>(json, JsonOpts) ?? new List<Track>();
        }

        public async Task AddFavoriteAsync(Track track)
        {
            var favs = await GetFavoritesAsync();
            if (!favs.Exists(f => f.Url == track.Url))
            {
                favs.Add(track);
                await SecureStorage.SetAsync(FavKey, JsonSerializer.Serialize(favs, JsonOpts));
            }
        }

        public async Task RemoveFavoriteAsync(string url)
        {
            var favs = await GetFavoritesAsync();
            favs.RemoveAll(f => f.Url == url || f.Id == url);
            await SecureStorage.SetAsync(FavKey, JsonSerializer.Serialize(favs, JsonOpts));
        }

        // ─────────────────────────────────────────────
        // HISTORY — Almacenado localmente en el dispositivo
        // ─────────────────────────────────────────────
        private const string HistKey = "emusic_history";

        public async Task<List<Track>> GetHistoryAsync()
        {
            var json = await SecureStorage.GetAsync(HistKey);
            if (string.IsNullOrEmpty(json)) return new List<Track>();
            return JsonSerializer.Deserialize<List<Track>>(json, JsonOpts) ?? new List<Track>();
        }

        public async Task AddHistoryAsync(Track track)
        {
            var hist = await GetHistoryAsync();
            hist.RemoveAll(h => h.Url == track.Url); // Elimina duplicados
            hist.Insert(0, track); // Añade al principio (más reciente primero)
            if (hist.Count > 50) hist = hist.GetRange(0, 50); // Máximo 50 entradas
            await SecureStorage.SetAsync(HistKey, JsonSerializer.Serialize(hist, JsonOpts));
        }
    }

    // Modelo para la respuesta de /streams/{videoId}
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
