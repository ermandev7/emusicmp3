using Android.OS;
using AndroidX.Media3.Common;
using AndroidX.Media3.Session;
using Google.Common.Util.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace eMusicApp.Platforms.Android
{
    /// <summary>
    /// Callback para MediaLibrarySession. Expone historial y favoritos a Android Auto
    /// y resuelve comandos de voz (OnAddMediaItems) buscando en la API.
    /// </summary>
    public class LibrarySessionCallback : MediaLibrarySession.Callback
    {
        private const string ROOT_ID = "ROOT";
        private const string HISTORY_ID = "HISTORY";
        private const string FAVORITES_ID = "FAVORITES";

        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = System.TimeSpan.FromSeconds(20)
        };

        // ── Browsing: Root ──
        public override IListenableFuture OnGetLibraryRoot(
            MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            MediaLibraryService.LibraryParams? libParams)
        {
            var root = new MediaItem.Builder()
                .SetMediaId(ROOT_ID)
                .SetMediaMetadata(new MediaMetadata.Builder()
                    .SetIsBrowsable(Java.Lang.Boolean.True)
                    .SetIsPlayable(Java.Lang.Boolean.False)
                    .SetMediaType(new Java.Lang.Integer((int)MediaMetadata.MediaTypeFolderMixed))
                    .SetTitle("eMusicApp")
                    .Build())
                .Build();

            return Futures.ImmediateFuture(
                MediaLibraryService.LibraryResult.OfItem(root, /* params */ null));
        }

        // ── Browsing: Children (categories or tracks) ──
        public override IListenableFuture OnGetChildren(
            MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            string parentId,
            int page, int pageSize,
            MediaLibraryService.LibraryParams? libParams)
        {
            if (parentId == ROOT_ID)
            {
                // Return two browsable folders
                var folders = new Java.Util.ArrayList();
                folders.Add(BuildFolder(HISTORY_ID, "Historial"));
                folders.Add(BuildFolder(FAVORITES_ID, "Favoritos"));
                return Futures.ImmediateFuture(
                    MediaLibraryService.LibraryResult.OfItemList(
                        (Java.Util.IList)folders, /* params */ null));
            }

            // Fetch tracks for the category asynchronously
            var future = SettableFuture.Create()!;
            Task.Run(async () =>
            {
                try
                {
                    var tracks = parentId == FAVORITES_ID
                        ? await FetchFavoritesAsync()
                        : await FetchHistoryAsync();

                    var items = new Java.Util.ArrayList();
                    foreach (var t in tracks.Take(30))
                        items.Add(BuildPlayableItem(t.videoId, t.title, t.artist, t.thumb));

                    future.Set(MediaLibraryService.LibraryResult.OfItemList(
                        (Java.Util.IList)items, /* params */ null));
                }
                catch
                {
                    future.Set(MediaLibraryService.LibraryResult.OfItemList(
                        (Java.Util.IList)new Java.Util.ArrayList(), /* params */ null));
                }
            });
            return future;
        }

        // ── Search (Android Auto search bar) ──
        public override IListenableFuture OnGetSearchResult(
            MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            string query,
            int page, int pageSize,
            MediaLibraryService.LibraryParams? libParams)
        {
            var future = SettableFuture.Create()!;
            Task.Run(async () =>
            {
                try
                {
                    var tracks = await SearchTracksAsync(query);
                    var items = new Java.Util.ArrayList();
                    foreach (var t in tracks.Take(20))
                        items.Add(BuildPlayableItem(t.videoId, t.title, t.artist, t.thumb));

                    future.Set(MediaLibraryService.LibraryResult.OfItemList(
                        (Java.Util.IList)items, /* params */ null));
                }
                catch
                {
                    future.Set(MediaLibraryService.LibraryResult.OfItemList(
                        (Java.Util.IList)new Java.Util.ArrayList(), /* params */ null));
                }
            });
            return future;
        }

        // ── Voice commands: "Play X on eMusicApp" ──
        // Media3 calls this to resolve MediaItems before playback.
        // The items come with RequestMetadata.SearchQuery from the voice command.
        public override IListenableFuture OnAddMediaItems(
            MediaSession session,
            MediaSession.ControllerInfo controller,
            Java.Util.IList mediaItems)
        {
            var future = SettableFuture.Create()!;
            Task.Run(async () =>
            {
                try
                {
                    var resolved = new Java.Util.ArrayList();

                    for (int i = 0; i < mediaItems.Size(); i++)
                    {
                        var item = (MediaItem)mediaItems.Get(i)!;
                        var query = item.RequestMetadata?.SearchQuery;

                        if (!string.IsNullOrEmpty(query))
                        {
                            // Voice command: search and resolve to playable item
                            var track = await SearchAndResolveAsync(query);
                            if (track != null)
                            {
                                resolved.Add(BuildResolvedItem(
                                    track.videoId, track.title, track.artist,
                                    track.thumb, track.streamUrl));
                            }
                        }
                        else if (!string.IsNullOrEmpty(item.MediaId))
                        {
                            // Tapped from Android Auto browse: resolve stream URL
                            var track = await ResolveStreamAsync(item.MediaId);
                            if (track != null)
                            {
                                resolved.Add(BuildResolvedItem(
                                    item.MediaId,
                                    track.title, track.artist,
                                    track.thumb, track.streamUrl));
                            }
                        }
                    }

                    future.Set((Java.Util.IList)resolved);
                }
                catch (System.Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[LibraryCallback] OnAddMediaItems error: {ex.Message}");
                    future.Set((Java.Util.IList)new Java.Util.ArrayList());
                }
            });
            return future;
        }

        // ── API calls ──

        private static async Task<List<(string videoId, string title, string artist, string thumb)>> FetchHistoryAsync()
        {
            var json = await _http.GetStringAsync($"{AppConstants.ApiBaseUrl}/history");
            return ParseTracks(json);
        }

        private static async Task<List<(string videoId, string title, string artist, string thumb)>> FetchFavoritesAsync()
        {
            var json = await _http.GetStringAsync($"{AppConstants.ApiBaseUrl}/favorites");
            return ParseTracks(json);
        }

        private static async Task<List<(string videoId, string title, string artist, string thumb)>> SearchTracksAsync(string query)
        {
            var url = $"{AppConstants.ApiBaseUrl}/search?q={System.Uri.EscapeDataString(query)}";
            var json = await _http.GetStringAsync(url);
            return ParseSearchResults(json);
        }

        private static async Task<(string videoId, string title, string artist, string thumb, string streamUrl)?> SearchAndResolveAsync(string query)
        {
            var tracks = await SearchTracksAsync(query);
            if (tracks.Count == 0) return null;

            var first = tracks[0];
            var streamUrl = await GetBestStreamUrlAsync(first.videoId);
            if (string.IsNullOrEmpty(streamUrl)) return null;

            return (first.videoId, first.title, first.artist, first.thumb, streamUrl);
        }

        private static async Task<(string title, string artist, string thumb, string streamUrl)?> ResolveStreamAsync(string videoId)
        {
            try
            {
                var json = await _http.GetStringAsync($"{AppConstants.ApiBaseUrl}/streams/{videoId}");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var title = root.GetProperty("title").GetString() ?? "";
                var artist = root.GetProperty("uploader").GetString() ?? "";
                var thumb = root.GetProperty("thumbnailUrl").GetString() ?? "";

                string? bestUrl = null;
                int bestBitrate = 0;
                foreach (var s in root.GetProperty("audioStreams").EnumerateArray())
                {
                    int br = s.TryGetProperty("bitrate", out var brEl) ? brEl.GetInt32() : 0;
                    string? su = s.TryGetProperty("url", out var suEl) ? suEl.GetString() : null;
                    if (su != null && br > bestBitrate) { bestBitrate = br; bestUrl = su; }
                }

                if (string.IsNullOrEmpty(bestUrl)) return null;
                return (title, artist, thumb, bestUrl);
            }
            catch { return null; }
        }

        private static async Task<string?> GetBestStreamUrlAsync(string videoId)
        {
            try
            {
                var json = await _http.GetStringAsync($"{AppConstants.ApiBaseUrl}/streams/{videoId}");
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string? bestUrl = null;
                int bestBitrate = 0;
                foreach (var s in root.GetProperty("audioStreams").EnumerateArray())
                {
                    int br = s.TryGetProperty("bitrate", out var brEl) ? brEl.GetInt32() : 0;
                    string? su = s.TryGetProperty("url", out var suEl) ? suEl.GetString() : null;
                    if (su != null && br > bestBitrate) { bestBitrate = br; bestUrl = su; }
                }
                return bestUrl;
            }
            catch { return null; }
        }

        // ── JSON parsing ──

        private static List<(string videoId, string title, string artist, string thumb)> ParseTracks(string json)
        {
            var result = new List<(string, string, string, string)>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                var arr = doc.RootElement.ValueKind == JsonValueKind.Array
                    ? doc.RootElement.EnumerateArray()
                    : (doc.RootElement.TryGetProperty("items", out var items)
                        ? items.EnumerateArray()
                        : doc.RootElement.EnumerateArray());

                foreach (var el in arr)
                {
                    var vid = el.TryGetProperty("videoId", out var vp) ? vp.GetString() : null;
                    if (string.IsNullOrEmpty(vid) && el.TryGetProperty("url", out var urlP))
                    {
                        var u = urlP.GetString() ?? "";
                        var idx = u.IndexOf("?v=");
                        if (idx >= 0) vid = u.Substring(idx + 3);
                    }
                    if (string.IsNullOrEmpty(vid)) continue;

                    var title = el.TryGetProperty("title", out var tp) ? tp.GetString() ?? "" : "";
                    var artist = el.TryGetProperty("uploaderName", out var ap) ? ap.GetString() ?? ""
                               : el.TryGetProperty("artist", out var ar) ? ar.GetString() ?? ""
                               : el.TryGetProperty("uploader", out var up) ? up.GetString() ?? "" : "";
                    var thumb = el.TryGetProperty("thumbnailUrl", out var thp) ? thp.GetString() ?? ""
                              : el.TryGetProperty("thumbnail", out var th2) ? th2.GetString() ?? "" : "";

                    result.Add((vid, title, artist, thumb));
                }
            }
            catch { }
            return result;
        }

        private static List<(string videoId, string title, string artist, string thumb)> ParseSearchResults(string json)
        {
            var result = new List<(string, string, string, string)>();
            try
            {
                using var doc = JsonDocument.Parse(json);
                JsonElement items;
                if (doc.RootElement.TryGetProperty("items", out items))
                { /* use items */ }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    items = doc.RootElement;
                else return result;

                foreach (var el in items.EnumerateArray())
                {
                    if (el.TryGetProperty("type", out var typeEl))
                    {
                        var type = typeEl.GetString();
                        if (!string.IsNullOrEmpty(type) && type != "stream") continue;
                    }

                    var vid = el.TryGetProperty("videoId", out var vp) ? vp.GetString() : null;
                    if (string.IsNullOrEmpty(vid) && el.TryGetProperty("url", out var urlP))
                    {
                        var u = urlP.GetString() ?? "";
                        var idx = u.IndexOf("?v=");
                        if (idx >= 0) vid = u.Substring(idx + 3);
                    }
                    if (string.IsNullOrEmpty(vid)) continue;

                    var title = el.TryGetProperty("title", out var tp) ? tp.GetString() ?? "" : "";
                    var artist = el.TryGetProperty("uploaderName", out var ap) ? ap.GetString() ?? ""
                               : el.TryGetProperty("artist", out var ar) ? ar.GetString() ?? ""
                               : el.TryGetProperty("uploader", out var up) ? up.GetString() ?? "" : "";
                    var thumb = el.TryGetProperty("thumbnailUrl", out var thp) ? thp.GetString() ?? ""
                              : el.TryGetProperty("thumbnail", out var th2) ? th2.GetString() ?? "" : "";

                    result.Add((vid, title, artist, thumb));
                }
            }
            catch { }
            return result;
        }

        // ── MediaItem builders ──

        private static MediaItem BuildFolder(string id, string title)
        {
            return new MediaItem.Builder()
                .SetMediaId(id)
                .SetMediaMetadata(new MediaMetadata.Builder()
                    .SetTitle(title)
                    .SetIsBrowsable(Java.Lang.Boolean.True)
                    .SetIsPlayable(Java.Lang.Boolean.False)
                    .SetMediaType(new Java.Lang.Integer((int)MediaMetadata.MediaTypeFolderMixed))
                    .Build())
                .Build();
        }

        private static MediaItem BuildPlayableItem(string videoId, string title, string artist, string thumbUrl)
        {
            var metaBuilder = new MediaMetadata.Builder()
                .SetTitle(title)
                .SetArtist(artist)
                .SetIsBrowsable(Java.Lang.Boolean.False)
                .SetIsPlayable(Java.Lang.Boolean.True)
                .SetMediaType(new Java.Lang.Integer((int)MediaMetadata.MediaTypeMusic));

            if (!string.IsNullOrEmpty(thumbUrl))
                metaBuilder.SetArtworkUri(global::Android.Net.Uri.Parse(thumbUrl));

            return new MediaItem.Builder()
                .SetMediaId(videoId)
                .SetMediaMetadata(metaBuilder.Build())
                .Build();
        }

        private static MediaItem BuildResolvedItem(string videoId, string title, string artist, string thumbUrl, string streamUrl)
        {
            var metaBuilder = new MediaMetadata.Builder()
                .SetTitle(title)
                .SetArtist(artist)
                .SetMediaType(new Java.Lang.Integer((int)MediaMetadata.MediaTypeMusic));

            if (!string.IsNullOrEmpty(thumbUrl))
                metaBuilder.SetArtworkUri(global::Android.Net.Uri.Parse(thumbUrl));

            return new MediaItem.Builder()
                .SetMediaId(videoId)
                .SetUri(streamUrl)
                .SetMediaMetadata(metaBuilder.Build())
                .Build();
        }
    }
}
