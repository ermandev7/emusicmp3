using System.Net.Http;
using System.Threading.Tasks;
using System;
using Microsoft.Extensions.Caching.Memory;

namespace eMusicApi.Services;

public class PipedApiService
{
    private readonly HttpClient _httpClient;
    private readonly IMemoryCache _cache;

    public PipedApiService(HttpClient httpClient, IMemoryCache cache)
    {
        _httpClient = httpClient;
        _cache = cache;
    }

    public async Task<string> SearchAsync(string query)
    {
        var cacheKey = $"search_{query}";
        if (_cache.TryGetValue(cacheKey, out string? cachedResult))
            return cachedResult;

        var response = await _httpClient.GetAsync($"/search?q={Uri.EscapeDataString(query)}&filter=all");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadAsStringAsync();
        
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(30)); // Caché de 30 mins
        return result;
    }

    public async Task<string> GetStreamAsync(string videoId)
    {
        var cacheKey = $"stream_{videoId}";
        if (_cache.TryGetValue(cacheKey, out string? cachedResult))
            return cachedResult;

        var response = await _httpClient.GetAsync($"/streams/{Uri.EscapeDataString(videoId)}");
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadAsStringAsync();
        
        _cache.Set(cacheKey, result, TimeSpan.FromMinutes(60)); // Caché de 60 mins
        return result;
    }

    public async Task<string> GetTrendingAsync()
    {
        var cacheKey = "trending_music";
        if (_cache.TryGetValue(cacheKey, out string? cachedResult))
            return cachedResult;

        var response = await _httpClient.GetAsync("/trending?region=US"); // Por ahora hardcoded region
        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadAsStringAsync();
        
        _cache.Set(cacheKey, result, TimeSpan.FromHours(1)); // Caché de 1 hora
        return result;
    }
}
