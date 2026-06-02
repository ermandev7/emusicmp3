using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Maui.Storage;

namespace eMusicApp
{
    public class DownloadedTrack
    {
        public string Id { get; set; } = "";
        public string Title { get; set; } = "";
        public string Artist { get; set; } = "";
        public string ThumbUrl { get; set; } = "";
        public string LocalPath { get; set; } = "";
    }

    public static class DownloadManager
    {
        private static readonly HttpClient _httpClient = new HttpClient();
        private static string BasePath => Path.Combine(FileSystem.AppDataDirectory, "offline_music");
        private static string MetadataPath => Path.Combine(BasePath, "metadata.json");

        private static List<DownloadedTrack> _tracks = new List<DownloadedTrack>();

        public static void Initialize()
        {
            if (!Directory.Exists(BasePath))
            {
                Directory.CreateDirectory(BasePath);
            }

            if (File.Exists(MetadataPath))
            {
                try
                {
                    string json = File.ReadAllText(MetadataPath);
                    _tracks = JsonSerializer.Deserialize<List<DownloadedTrack>>(json) ?? new List<DownloadedTrack>();
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error loading metadata: " + ex.Message);
                    _tracks = new List<DownloadedTrack>();
                }
            }
        }

        private static void SaveMetadata()
        {
            try
            {
                string json = JsonSerializer.Serialize(_tracks);
                File.WriteAllText(MetadataPath, json);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error saving metadata: " + ex.Message);
            }
        }

        public static async Task<bool> DownloadTrackAsync(string id, string url, string title, string artist, string thumbUrl)
        {
            try
            {
                if (_tracks.Find(t => t.Id == id) != null) return true; // Already downloaded

                string fileName = $"{id}.mp3";
                string localPath = Path.Combine(BasePath, fileName);

                using (var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    using (var streamToReadFrom = await response.Content.ReadAsStreamAsync())
                    {
                        using (var streamToWriteTo = File.Open(localPath, FileMode.Create))
                        {
                            await streamToReadFrom.CopyToAsync(streamToWriteTo);
                        }
                    }
                }

                var track = new DownloadedTrack
                {
                    Id = id,
                    Title = title,
                    Artist = artist,
                    ThumbUrl = thumbUrl,
                    LocalPath = localPath
                };

                _tracks.Add(track);
                SaveMetadata();
                
                // Notify UI that download finished
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    try { 
                        var activity = Microsoft.Maui.ApplicationModel.Platform.CurrentActivity;
                        // We will use the WebView to notify if possible, but for now we just save it.
                    } catch { }
                });

                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error downloading {id}: {ex.Message}");
                return false;
            }
        }

        public static bool DeleteTrack(string id)
        {
            var track = _tracks.Find(t => t.Id == id);
            if (track != null)
            {
                try
                {
                    if (File.Exists(track.LocalPath))
                    {
                        File.Delete(track.LocalPath);
                    }
                    _tracks.Remove(track);
                    SaveMetadata();
                    return true;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Error deleting track: " + ex.Message);
                    return false;
                }
            }
            return false;
        }

        public static string? GetLocalPath(string id)
        {
            var track = _tracks.Find(t => t.Id == id);
            if (track != null && File.Exists(track.LocalPath))
            {
                return track.LocalPath;
            }
            return null;
        }

        public static string GetDownloadedTracksJson()
        {
            return JsonSerializer.Serialize(_tracks);
        }

        public static List<DownloadedTrack> GetDownloadedTracks()
        {
            return new List<DownloadedTrack>(_tracks);
        }

        public static bool IsTrackDownloaded(string id)
        {
            return _tracks.Exists(t => t.Id == id);
        }
    }
}
