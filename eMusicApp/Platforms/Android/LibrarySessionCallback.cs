using Android.OS;
using AndroidX.Concurrent.Futures;
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
    public class LibrarySessionCallback : MediaLibraryService.MediaLibrarySession.Callback
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
            MediaLibraryService.MediaLibrarySession session,
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

            return CreateImmediateFuture(
                MediaLibraryService.LibraryResult.OfItem(root, null));
        }

        // ── Browsing: Children (categories or tracks) ──
        public override IListenableFuture OnGetChildren(
            MediaLibraryService.MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            string parentId,
            int page, int pageSize,
            MediaLibraryService.LibraryParams? libParams)
        {
            if (parentId == ROOT_ID)
            {
                var folders = new Java.Util.ArrayList();
                folders.Add(BuildFolder(HISTORY_ID, "Historial"));
                folders.Add(BuildFolder(FAVORITES_ID, "Favoritos"));
                return CreateImmediateFuture(
                    MediaLibraryService.LibraryResult.OfItemList(
                        (Java.Util.IList)folders, null));
            }

            return CreateAsyncFuture(async () =>
            {
                var tracks = parentId == FAVORITES_ID
                    ? await FetchFavoritesAsync()
                    : await FetchHistoryAsync();

                var items = new Java.Util.ArrayList();
                foreach (var t in tracks.Take(30))
                    items.Add(BuildPlayableItem(t.videoId, t.title, t.artist, t.thumb));

                return (Java.Lang.Object)MediaLibraryService.LibraryResult.OfItemList(
                    (Java.Util.IList)items, null);
            });
        }

        // ── Search (Android Auto search bar) ──
        public override IListenableFuture OnGetSearchResult(
            MediaLibraryService.MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            string query,
            int page, int pageSize,
            MediaLibraryService.LibraryParams? libParams)
        {
            return CreateAsyncFuture(async () =>
            {
                var tracks = await SearchTracksAsync(query);
                var items = new Java.Util.ArrayList();
                foreach (var t in tracks.Take(20))
                    items.Add(BuildPlayableItem(t.videoId, t.title, t.artist, t.thumb));

                return (Java.Lang.Object)MediaLibraryService.LibraryResult.OfItemList(
                    (Java.Util.IList)items, null);
            });
        }

        // ── Voice commands: "Play X on eMusicApp" ──
        public override IListenableFuture OnAddMediaItems(
            MediaSession session,
            MediaSession.ControllerInfo controller,
            Java.Util.IList mediaItems)
        {
            return CreateAsyncFuture(async () =>
            {
                var resolved = new Java.Util.ArrayList();

                for (int i = 0; i < mediaItems.Size(); i++)
                {
                    var item = (MediaItem)mediaItems.Get(i)!;
                    var query = item.RequestMetadata?.SearchQuery;

                    if (!string.IsNullOrEmpty(query))
                    {
                        var track = await SearchAndResolveAsync(query);
                        if (track != null)
                        {
                            resolved.Add(BuildResolvedItem(
                                track.Value.videoId, track.Value.title, track.Value.artist,
                                track.Value.thumb, track.Value.streamUrl));
                        }
                    }
                    else if (!string.IsNullOrEmpty(item.MediaId))
                    {
                        var track = await ResolveStreamAsync(item.MediaId);
                        if (track != null)
                        {
                            resolved.Add(BuildResolvedItem(
                                item.MediaId,
                                track.Value.title, track.Value.artist,
                                track.Value.thumb, track.Value.streamUrl));
                        }
                    }
                }

                return (Java.Lang.Object)(Java.Util.IList)resolved;
            });
        }

        // ── Future helpers ──

        private static IListenableFuture CreateImmediateFuture(Java.Lang.Object result)
        {
            return CallbackToFutureAdapter.GetFuture(new ImmediateResolver(result));
        }

        private static IListenableFuture CreateAsyncFuture(System.Func<Task<Java.Lang.Object>> asyncFunc)
        {
            return CallbackToFutureAdapter.GetFuture(new AsyncResolver(asyncFunc));
        }

        private class ImmediateResolver : Java.Lang.Object, CallbackToFutureAdapter.IResolver
        {
            private readonly Java.Lang.Object _result;
            public ImmediateResolver(Java.Lang.Object result) => _result = result;
            public Java.Lang.Object? AttachCompleter(CallbackToFutureAdapter.Completer completer)
            {
                completer.Set(_result);
                return null;
            }
        }

        private class AsyncResolver : Java.Lang.Object, CallbackToFutureAdapter.IResolver
        {
            private readonly System.Func<Task<Java.Lang.Object>> _func;
            public AsyncResolver(System.Func<Task<Java.Lang.Object>> func) => _func = func;
            public Java.Lang.Object? AttachCompleter(CallbackToFutureAdapter.Completer completer)
            {
                Task.Run(async () =>
                {
                    try
                    {
                        var result = await _func();
                        completer.Set(result);
                    }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LibraryCallback] Error: {ex.Message}");
                        completer.Set(new Java.Util.ArrayList()); // empty fallback
                    }
                });
                return null;
            }
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
                string? bestUrl = null;
                int bestBitrate = 0;
                foreach (var s in doc.RootElement.GetProperty("audioStreams").EnumerateArray())
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
                JsonElement.ArrayEnumerator arr;
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    arr = doc.RootElement.EnumerateArray();
                else if (doc.RootElement.TryGetProperty("items", out var items))
                    arr = items.EnumerateArray();
                else return result;

                foreach (var el in arr)
                {
                    var vid = ExtractVideoId(el);
                    if (string.IsNullOrEmpty(vid)) continue;
                    result.Add((vid, GetString(el, "title"), GetArtist(el), GetThumb(el)));
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
                if (doc.RootElement.TryGetProperty("items", out items)) { }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array) items = doc.RootElement;
                else return result;

                foreach (var el in items.EnumerateArray())
                {
                    if (el.TryGetProperty("type", out var typeEl))
                    {
                        var type = typeEl.GetString();
                        if (!string.IsNullOrEmpty(type) && type != "stream") continue;
                    }
                    var vid = ExtractVideoId(el);
                    if (string.IsNullOrEmpty(vid)) continue;
                    result.Add((vid, GetString(el, "title"), GetArtist(el), GetThumb(el)));
                }
            }
            catch { }
            return result;
        }

        private static string? ExtractVideoId(JsonElement el)
        {
            if (el.TryGetProperty("videoId", out var vp))
            {
                var v = vp.GetString();
                if (!string.IsNullOrEmpty(v)) return v;
            }
            if (el.TryGetProperty("url", out var urlP))
            {
                var u = urlP.GetString() ?? "";
                var idx = u.IndexOf("?v=");
                if (idx >= 0) return u.Substring(idx + 3);
            }
            return null;
        }

        private static string GetString(JsonElement el, string prop)
            => el.TryGetProperty(prop, out var p) ? p.GetString() ?? "" : "";

        private static string GetArtist(JsonElement el)
            => GetString(el, "uploaderName") is { Length: > 0 } u ? u
             : GetString(el, "artist") is { Length: > 0 } a ? a
             : GetString(el, "uploader");

        private static string GetThumb(JsonElement el)
            => GetString(el, "thumbnailUrl") is { Length: > 0 } t ? t
             : GetString(el, "thumbnail");

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
            var meta = new MediaMetadata.Builder()
                .SetTitle(title)
                .SetArtist(artist)
                .SetIsBrowsable(Java.Lang.Boolean.False)
                .SetIsPlayable(Java.Lang.Boolean.True)
                .SetMediaType(new Java.Lang.Integer((int)MediaMetadata.MediaTypeMusic));
            if (!string.IsNullOrEmpty(thumbUrl))
                meta.SetArtworkUri(global::Android.Net.Uri.Parse(thumbUrl));
            return new MediaItem.Builder()
                .SetMediaId(videoId)
                .SetMediaMetadata(meta.Build())
                .Build();
        }

        private static MediaItem BuildResolvedItem(string videoId, string title, string artist, string thumbUrl, string streamUrl)
        {
            var meta = new MediaMetadata.Builder()
                .SetTitle(title)
                .SetArtist(artist)
                .SetMediaType(new Java.Lang.Integer((int)MediaMetadata.MediaTypeMusic));
            if (!string.IsNullOrEmpty(thumbUrl))
                meta.SetArtworkUri(global::Android.Net.Uri.Parse(thumbUrl));
            return new MediaItem.Builder()
                .SetMediaId(videoId)
                .SetUri(streamUrl)
                .SetMediaMetadata(meta.Build())
                .Build();
        }
    }
}
