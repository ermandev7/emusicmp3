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
            Timeout = System.TimeSpan.FromSeconds(15) // Cada llamada API tarda ~5-7s, y hacemos 2 secuenciales
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
        {
            // Capturar datos del player en el main thread ANTES de ir a background
            MediaItem? currentItem = null;
            int currentIndex = 0;
            long currentPosition = 0;
            try
            {
                var player = mediaSession.Player;
                if (player?.CurrentMediaItem != null)
                {
                    currentItem = player.CurrentMediaItem;
                    currentIndex = player.CurrentMediaItemIndex;
                    currentPosition = player.CurrentPosition;
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionCallback] Error reading player state: {ex.Message}");
            }

            if (currentItem != null)
            {
                var list = new List<MediaItem> { currentItem };
                return CreateImmediateFuture(new MediaSession.MediaItemsWithStartPosition(
                    list, currentIndex, currentPosition));
            }

            // Sin canción actual — buscar algo popular como fallback (en background)
            return CreateAsyncFuture(async () =>
            {
                var track = await SearchAndResolveAsync("música popular mix");
                if (track != null)
                {
                    var list = new List<MediaItem>
                    {
                        BuildResolvedItem(track.Value.videoId, track.Value.title,
                            track.Value.artist, track.Value.thumb, track.Value.streamUrl)
                    };
                    return new MediaSession.MediaItemsWithStartPosition(list, 0, 0);
                }
                return new MediaSession.MediaItemsWithStartPosition(new List<MediaItem>(), 0, 0);
            });
        }

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
                    System.Diagnostics.Debug.WriteLine($"[SessionCallback] OnAddMediaItems query='{query}' mediaId='{item.MediaId}'");

                    if (!string.IsNullOrEmpty(query))
                    {
                        var track = await SearchAndResolveAsync(query);
                        if (track != null)
                        {
                            resolved.Add(BuildResolvedItem(
                                track.Value.videoId, track.Value.title, track.Value.artist,
                                track.Value.thumb, track.Value.streamUrl));
                            System.Diagnostics.Debug.WriteLine($"[SessionCallback] Resolved: {track.Value.title}");
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine($"[SessionCallback] Search failed for: {query}");
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
                    else
                    {
                        // Fallback: intentar con el título del MediaItem como query
                        var title = item.MediaMetadata?.Title?.ToString();
                        if (!string.IsNullOrEmpty(title))
                        {
                            System.Diagnostics.Debug.WriteLine($"[SessionCallback] Fallback search by title: {title}");
                            var track = await SearchAndResolveAsync(title);
                            if (track != null)
                            {
                                resolved.Add(BuildResolvedItem(
                                    track.Value.videoId, track.Value.title, track.Value.artist,
                                    track.Value.thumb, track.Value.streamUrl));
                            }
                        }
                    }
                }

                System.Diagnostics.Debug.WriteLine($"[SessionCallback] Resolved {resolved.Count} items");
                // Media3 espera IList<MediaItem> — usar List<MediaItem> directamente
                // wrapeado en Java.Util.ArrayList para compatibilidad con IListenableFuture
                var javaList = new Java.Util.ArrayList();
                foreach (var r in resolved) javaList.Add(r);

                // Si no se resolvió nada, intentar fallback con el texto completo del primer item
                if (resolved.Count == 0 && mediaItems.Count > 0)
                {
                    System.Diagnostics.Debug.WriteLine("[SessionCallback] All items failed, trying generic fallback");
                    var fallback = await SearchAndResolveAsync("música popular");
                    if (fallback != null)
                    {
                        javaList.Add(BuildResolvedItem(
                            fallback.Value.videoId, fallback.Value.title,
                            fallback.Value.artist, fallback.Value.thumb, fallback.Value.streamUrl));
                    }
                }

                return (Java.Lang.Object)javaList;
            });
        }

        /// <summary>
        /// Accede al campo Java requestMetadata.searchQuery via JNI,
        /// ya que los bindings .NET no exponen RequestMetadata como propiedad de MediaItem.
        /// </summary>
        private static string? GetSearchQuery(MediaItem item)
        {
            // Intento 1: JNI — acceder a requestMetadata.searchQuery
            try
            {
                var itemClass = Java.Lang.Class.FromType(typeof(MediaItem));
                var field = itemClass.GetField("requestMetadata");
                var reqMeta = field?.Get(item);
                if (reqMeta != null)
                {
                    var reqMetaClass = reqMeta.Class;
                    var searchField = reqMetaClass.GetField("searchQuery");
                    var searchQuery = searchField?.Get(reqMeta)?.ToString();
                    if (!string.IsNullOrEmpty(searchQuery))
                    {
                        System.Diagnostics.Debug.WriteLine($"[SessionCallback] JNI searchQuery: '{searchQuery}'");
                        return searchQuery;
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionCallback] JNI failed: {ex.Message}");
            }

            // Intento 2: título del metadata como query de búsqueda
            try
            {
                var title = item.MediaMetadata?.Title?.ToString();
                if (!string.IsNullOrEmpty(title))
                {
                    System.Diagnostics.Debug.WriteLine($"[SessionCallback] Using metadata title as query: '{title}'");
                    return title;
                }
            }
            catch { }

            // Intento 3: mediaId como query (a veces contiene el nombre de la canción)
            try
            {
                var mediaId = item.MediaId;
                if (!string.IsNullOrEmpty(mediaId) && mediaId.Length > 15)
                {
                    // Solo si parece texto, no un videoId corto
                    System.Diagnostics.Debug.WriteLine($"[SessionCallback] Using mediaId as query: '{mediaId}'");
                    return mediaId;
                }
            }
            catch { }

            return null;
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
                        // Timeout global de 25s — la API tarda ~5-7s por llamada, hacemos 2 secuenciales
                        using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(25));
                        var task = _func();
                        var completed = await Task.WhenAny(task, Task.Delay(-1, cts.Token));
                        if (completed == task)
                        {
                            completer.Set(await task);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[SessionCallback] Global timeout (25s) reached");
                            completer.Set(new Java.Util.ArrayList());
                        }
                    }
                    catch (System.OperationCanceledException)
                    {
                        System.Diagnostics.Debug.WriteLine("[SessionCallback] Operation cancelled (timeout)");
                        completer.Set(new Java.Util.ArrayList());
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
            var result = new List<(string, string, string, string)>();
            try
            {
                var url = $"{AppConstants.ApiBaseUrl}/search?q={System.Uri.EscapeDataString(query)}";
                System.Diagnostics.Debug.WriteLine($"[SessionCallback] Searching: {url}");
                var json = await _http.GetStringAsync(url);

                using var doc = JsonDocument.Parse(json);
                JsonElement items;
                if (doc.RootElement.TryGetProperty("items", out items)) { }
                else if (doc.RootElement.ValueKind == JsonValueKind.Array) items = doc.RootElement;
                else
                {
                    System.Diagnostics.Debug.WriteLine("[SessionCallback] Search response has no 'items' and is not array");
                    return result;
                }

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
                System.Diagnostics.Debug.WriteLine($"[SessionCallback] Search found {result.Count} tracks");
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SessionCallback] Search error for '{query}': {ex.Message}");
            }
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
                if (idx >= 0)
                {
                    var id = u.Substring(idx + 3);
                    var ampIdx = id.IndexOf('&');
                    return ampIdx >= 0 ? id.Substring(0, ampIdx) : id;
                }
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
