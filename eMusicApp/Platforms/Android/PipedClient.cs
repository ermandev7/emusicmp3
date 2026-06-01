using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Collections.Generic;
using Android.Support.V4.Media;

namespace eMusicApp.Platforms.Android
{
    public static class PipedClient
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private const string ApiBase = "https://api.emusicmp3.duckdns.org";

        public static async Task<List<MediaBrowserCompat.MediaItem>> GetTrendingAsync()
        {
            var items = new List<MediaBrowserCompat.MediaItem>();
            try
            {
                var response = await _httpClient.GetStringAsync($"{ApiBase}/trending?filter=music_songs&region=US");
                using var document = JsonDocument.Parse(response);
                
                // Usually returns an array or an object with items. Let's assume an array of objects.
                if (document.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in document.RootElement.EnumerateArray())
                    {
                        var url = item.GetProperty("url").GetString();
                        if (string.IsNullOrEmpty(url)) continue;

                        var match = System.Text.RegularExpressions.Regex.Match(url, @"v=([^&]+)");
                        if (!match.Success) continue;
                        var videoId = match.Groups[1].Value;

                        var title = item.TryGetProperty("title", out var titleProp) ? titleProp.GetString() : "Unknown";
                        var uploaderName = item.TryGetProperty("uploaderName", out var uploaderProp) ? uploaderProp.GetString() : "Unknown";
                        var thumbnailUrl = item.TryGetProperty("thumbnail", out var thumbProp) ? thumbProp.GetString() : "";

                        var description = new MediaDescriptionCompat.Builder()
                            .SetMediaId(videoId)
                            .SetTitle(title)
                            .SetSubtitle(uploaderName)
                            .SetIconUri(global::Android.Net.Uri.Parse(thumbnailUrl))
                            .Build();

                        items.Add(new MediaBrowserCompat.MediaItem(description, MediaBrowserCompat.MediaItem.FlagPlayable));
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in GetTrendingAsync", ex);
                Console.WriteLine($"Error in GetTrendingAsync: {ex.Message}");
            }
            return items;
        }

        public static async Task<string?> GetStreamUrlAsync(string videoId)
        {
            try
            {
                var response = await _httpClient.GetStringAsync($"{ApiBase}/streams/{videoId}");
                using var document = JsonDocument.Parse(response);
                
                var root = document.RootElement;
                if (root.TryGetProperty("audioStreams", out var audioStreams) && audioStreams.GetArrayLength() > 0)
                {
                    // Find the first audio stream
                    return audioStreams[0].GetProperty("url").GetString();
                }
                
                if (root.TryGetProperty("videoStreams", out var videoStreams) && videoStreams.GetArrayLength() > 0)
                {
                    // Fallback to video stream
                    return videoStreams[0].GetProperty("url").GetString();
                }
            }
            catch (Exception ex)
            {
                Logger.Log($"Error in GetStreamUrlAsync for {videoId}", ex);
                Console.WriteLine($"Error in GetStreamUrlAsync: {ex.Message}");
            }
            Logger.Log($"GetStreamUrlAsync failed to find stream for {videoId}");
            return null;
        }
    }
}
