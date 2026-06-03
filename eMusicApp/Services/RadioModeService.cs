using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using eMusicApp.Models;

namespace eMusicApp.Services;

public class RadioModeService
{
    private readonly HttpClient _httpClient;
    private readonly ApiService _apiService;

    // Cache de pre-fetch
    private List<Track> _prefetchedTracks = new();
    private string? _prefetchedForVideoId;
    private bool _isFetching;

    private static readonly Regex TitleCleanRegex = new(
        @"\s*[\(\[].*?[\)\]]|\s*[-–|].*?(official|video|audio|lyrics|ft\.|feat\.|hd|4k|remaster).*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public RadioModeService(ApiService apiService)
    {
        _apiService = apiService;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
    }

    /// <summary>
    /// Obtiene tracks similares vía Last.fm y los busca en YouTube a través de nuestra API.
    /// </summary>
    public async Task<List<Track>> GetSimilarTracksAsync(
        string artist, string title, HashSet<string> excludeIds, int limit = 5)
    {
        var results = new List<Track>();
        try
        {
            var pairs = await GetSimilarFromLastFmRawAsync(_httpClient, artist, title, limit);
            if (pairs.Count == 0) return results;

            foreach (var (recArtist, recTrack) in pairs)
            {
                try
                {
                    var searchResults = await _apiService.SearchTracksAsync($"{recArtist} {recTrack}");
                    var match = searchResults.FirstOrDefault(t =>
                        !string.IsNullOrEmpty(t.VideoId) && !excludeIds.Contains(t.VideoId));
                    if (match != null)
                        results.Add(match);
                }
                catch { }

                if (results.Count >= limit) break;
            }

            System.Diagnostics.Debug.WriteLine(
                $"[Radio Last.fm] '{artist} - {title}' → {pairs.Count} sugerencias, {results.Count} encontradas en YouTube");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Radio Last.fm] Error: {ex.Message}");
        }
        return results;
    }

    /// <summary>
    /// Pre-carga recomendaciones al 50% de reproducción. Seguro llamar múltiples veces.
    /// </summary>
    public async Task PrefetchSimilarAsync(
        string videoId, string artist, string title, HashSet<string> excludeIds)
    {
        if (_isFetching || _prefetchedForVideoId == videoId) return;
        _isFetching = true;
        try
        {
            var tracks = await GetSimilarTracksAsync(artist, title, excludeIds);
            _prefetchedTracks = tracks;
            _prefetchedForVideoId = videoId;
        }
        finally { _isFetching = false; }
    }

    /// <summary>
    /// Consume los resultados pre-cargados. Devuelve lista vacía si no hay o no coincide el videoId.
    /// </summary>
    public List<Track> ConsumePrefetched(string videoId, HashSet<string> excludeIds)
    {
        if (_prefetchedForVideoId != videoId || _prefetchedTracks.Count == 0)
            return new List<Track>();

        var result = _prefetchedTracks
            .Where(t => !excludeIds.Contains(t.VideoId))
            .ToList();

        _prefetchedTracks = new List<Track>();
        _prefetchedForVideoId = null;
        return result;
    }

    /// <summary>
    /// Llama a Last.fm track.getSimilar y devuelve pares (artista, canción).
    /// Método estático para uso desde el servicio nativo Android (sin DI).
    /// </summary>
    public static async Task<List<(string artist, string track)>> GetSimilarFromLastFmRawAsync(
        HttpClient client, string artist, string title, int limit = 5)
    {
        var results = new List<(string artist, string track)>();

        var cleanTitle = CleanTitle(title);
        if (string.IsNullOrWhiteSpace(cleanTitle) || string.IsNullOrWhiteSpace(artist))
            return results;

        var url = $"http://ws.audioscrobbler.com/2.0/?method=track.getSimilar" +
                  $"&artist={Uri.EscapeDataString(artist)}" +
                  $"&track={Uri.EscapeDataString(cleanTitle)}" +
                  $"&api_key={AppConstants.LastFmApiKey}" +
                  $"&format=json&limit={limit}";

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var json = await client.GetStringAsync(url, cts.Token);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("similartracks", out var st)
            && st.TryGetProperty("track", out var tracks))
        {
            foreach (var t in tracks.EnumerateArray())
            {
                var trackName = t.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                var artistName = t.TryGetProperty("artist", out var artEl)
                    && artEl.TryGetProperty("name", out var artNameEl) ? artNameEl.GetString() : null;

                if (!string.IsNullOrEmpty(trackName) && !string.IsNullOrEmpty(artistName))
                    results.Add((artistName, trackName));
            }
        }

        return results;
    }

    /// <summary>
    /// Limpia el título quitando "(Official Video)", "[Lyrics]", "ft. Artist", etc.
    /// </summary>
    public static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        return TitleCleanRegex.Replace(title, "").Trim();
    }
}
