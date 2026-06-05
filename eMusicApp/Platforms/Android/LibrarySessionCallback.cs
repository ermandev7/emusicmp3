using Android.Content;
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
    /// Callback para MediaSession. Resuelve comandos de voz ("Play X on eMusicApp")
    /// buscando en la API y devolviendo MediaItems con stream URL.
    /// </summary>
    public class SessionCallback : Java.Lang.Object, MediaSession.ICallback
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = System.TimeSpan.FromSeconds(20)
        };

        // ── Aceptar conexiones de Android Auto y otros controladores ──
        public MediaSession.ConnectionResult OnConnect(
            MediaSession session,
            MediaSession.ControllerInfo controller)
        {
            return MediaSession.ConnectionResult.Accept(
                MediaSession.ConnectionResult.DefaultSessionCommands,
                MediaSession.ConnectionResult.DefaultPlayerCommands);
        }

        public void OnPostConnect(MediaSession session, MediaSession.ControllerInfo controller) { }
        public void OnDisconnected(MediaSession session, MediaSession.ControllerInfo controller) { }
        public void OnPlayerInteractionFinished(MediaSession session, MediaSession.ControllerInfo controllerInfo, PlayerCommands playerCommands) { }

        public int OnPlayerCommandRequest(MediaSession session, MediaSession.ControllerInfo controller, int playerCommand)
            => playerCommand; // Allow all commands

        public bool OnMediaButtonEvent(MediaSession session, MediaSession.ControllerInfo controllerInfo, Intent intent)
            => false; // Not handled, let default behavior

        public IListenableFuture OnCustomCommand(MediaSession session, MediaSession.ControllerInfo controller, SessionCommand customCommand, Bundle args)
            => CreateImmediateFuture(new SessionResult(SessionResult.ResultErrorNotSupported));

        public IListenableFuture OnCustomCommand(MediaSession session, MediaSession.ControllerInfo controller, SessionCommand customCommand, Bundle args, MediaSession.IProgressReporter progressReporter)
            => CreateImmediateFuture(new SessionResult(SessionResult.ResultErrorNotSupported));

        public IListenableFuture OnSetMediaItems(MediaSession mediaSession, MediaSession.ControllerInfo controller, IList<MediaItem> mediaItems, int startIndex, long startPositionMs)
            => OnAddMediaItems(mediaSession, controller, mediaItems);

        public IListenableFuture OnSetRating(MediaSession session, MediaSession.ControllerInfo controller, Rating rating)
            => CreateImmediateFuture(new SessionResult(SessionResult.ResultErrorNotSupported));

        public IListenableFuture OnSetRating(MediaSession session, MediaSession.ControllerInfo controller, string mediaId, Rating rating)
            => CreateImmediateFuture(new SessionResult(SessionResult.ResultErrorNotSupported));

        public IListenableFuture OnPlaybackResumption(MediaSession mediaSession, MediaSession.ControllerInfo controller)
            => CreateImmediateFuture(new SessionResult(SessionResult.ResultErrorNotSupported));

        public IListenableFuture OnPlaybackResumption(MediaSession mediaSession, MediaSession.ControllerInfo controller, bool isForPlayback)
            => OnPlaybackResumption(mediaSession, controller);

        // ── Voice commands: "Play X on eMusicApp" ──
        public IListenableFuture OnAddMediaItems(
            MediaSession session,
            MediaSession.ControllerInfo controller,
            IList<MediaItem> mediaItems)
        {
            return CreateAsyncFuture(async () =>
            {
                var resolved = new List<MediaItem>();

                foreach (var item in mediaItems)
                {
                    var query = GetSearchQuery(item);

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

                var javaList = new Java.Util.ArrayList();
                foreach (var r in resolved) javaList.Add(r);
                return (Java.Lang.Object)javaList;
            });
        }

        /// <summary>
        /// Accede al campo Java requestMetadata.searchQuery via JNI,
        /// ya que los bindings .NET no exponen RequestMetadata como propiedad de MediaItem.
        /// </summary>
        private static string? GetSearchQuery(MediaItem item)
        {
            try
            {
                var itemClass = Java.Lang.Class.FromType(typeof(MediaItem));
                var field = itemClass.GetField("requestMetadata");
                var reqMeta = field?.Get(item);
                if (reqMeta == null) return null;

                var reqMetaClass = reqMeta.Class;
                var searchField = reqMetaClass.GetField("searchQuery");
                var searchQuery = searchField?.Get(reqMeta)?.ToString();
                return string.IsNullOrEmpty(searchQuery) ? null : searchQuery;
            }
            catch
            {
                return null;
            }
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
                        System.Diagnostics.Debug.WriteLine($"[SessionCallback] Error: {ex.Message}");
                        completer.Set(new Java.Util.ArrayList());
                    }
                });
                return null;
            }
        }

        // ── API calls ──

        private static async Task<(string videoId, string title, string artist, string thumb, string streamUrl)?> SearchAndResolveAsync(string query)
        {
            var tracks = await SearchTracksAsync(query);
            if (tracks.Count == 0) return null;
            var first = tracks[0];
            var streamUrl = await GetBestStreamUrlAsync(first.videoId);
            if (string.IsNullOrEmpty(streamUrl)) return null;
            return (first.videoId, first.title, first.artist, first.thumb, streamUrl);
        }

        private static async Task<List<(string videoId, string title, string artist, string thumb)>> SearchTracksAsync(string query)
        {
            var url = $"{AppConstants.ApiBaseUrl}/search?q={System.Uri.EscapeDataString(query)}";
            var json = await _http.GetStringAsync(url);
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
                    var title = el.TryGetProperty("title", out var tp) ? tp.GetString() ?? "" : "";
                    var artist = el.TryGetProperty("uploaderName", out var up) ? up.GetString() ?? ""
                               : el.TryGetProperty("uploader", out var up2) ? up2.GetString() ?? "" : "";
                    var thumb = el.TryGetProperty("thumbnailUrl", out var thp) ? thp.GetString() ?? ""
                              : el.TryGetProperty("thumbnail", out var thp2) ? thp2.GetString() ?? "" : "";
                    result.Add((vid, title, artist, thumb));
                }
            }
            catch { }
            return result;
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

        // ── MediaItem builder ──

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
