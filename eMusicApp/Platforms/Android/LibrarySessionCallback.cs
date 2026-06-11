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

using MLS = AndroidX.Media3.Session.MediaLibraryService;

namespace eMusicApp.Platforms.Android
{
    /// <summary>
    /// Callback para MediaLibrarySession (Media3).
    ///
    /// ╔═══════════════════════════════════════════════════════════════════════╗
    /// ║  EQUIVALENCIA Media3 vs MediaBrowserServiceCompat (legacy)          ║
    /// ║                                                                     ║
    /// ║  Media3 (lo que usamos)          │ Legacy (lo que piden tutoriales) ║
    /// ║  ────────────────────────────────┼──────────────────────────────── ║
    /// ║  MediaLibraryService             │ MediaBrowserServiceCompat        ║
    /// ║  MediaLibrarySession.ICallback   │ MediaBrowserServiceCompat       ║
    /// ║  OnGetLibraryRoot()              │ onGetRoot()                      ║
    /// ║  OnGetChildren()                 │ onLoadChildren()                 ║
    /// ║  MediaMetadata.SetIsBrowsable()  │ FLAG_BROWSABLE                   ║
    /// ║  MediaMetadata.SetIsPlayable()   │ FLAG_PLAYABLE                    ║
    /// ║  OnSetMediaItems()               │ MediaSession.Callback.onPlay*()  ║
    /// ║                                                                     ║
    /// ║  Media3 es la API moderna de Google. MediaBrowserServiceCompat      ║
    /// ║  está deprecada. Android Auto soporta ambas.                       ║
    /// ╚═══════════════════════════════════════════════════════════════════════╝
    ///
    /// Árbol de medios que ve el conductor en la pantalla del coche:
    ///
    ///   📂 Root (emusic_root)
    ///   ├── 📂 Géneros (emusic_genres)         → Browsable ONLY
    ///   │   ├── 🎵📂 Salsa (genre:salsa)       → Browsable + Playable
    ///   │   │   ├── 🎵 Track 1 (videoId)       → Playable ONLY
    ///   │   │   ├── 🎵 Track 2 (videoId)       → Playable ONLY
    ///   │   │   └── ...
    ///   │   ├── 🎵📂 Rock (genre:rock)
    ///   │   └── ...
    ///   └── 📂 Tendencias (emusic_trending)    → Browsable ONLY
    ///       ├── 🎵 Track 1 (videoId)           → Playable ONLY
    ///       └── ...
    /// </summary>
    public class LibraryCallback : Java.Lang.Object, MLS.MediaLibrarySession.ICallback
    {
        private static readonly HttpClient _http = CreateHttpClient();

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = System.TimeSpan.FromSeconds(15) };
            var userId = Preferences.Default.Get("user_id", "");
            if (!string.IsNullOrEmpty(userId))
                client.DefaultRequestHeaders.Add("X-User-Id", userId);
            return client;
        }

        // ══════════════════════════════════════════════
        //  IDs del árbol de medios
        // ══════════════════════════════════════════════

        private const string ROOT_ID     = "emusic_root";
        private const string GENRES_ID   = "emusic_genres";
        private const string TRENDING_ID = "emusic_trending";
        private const string GENRE_PREFIX = "genre:";

        // Géneros disponibles: (id, nombre para mostrar, query de búsqueda)
        private static readonly (string id, string name, string query)[] Genres =
        {
            ("salsa",      "Salsa",      "salsa éxitos"),
            ("bachata",    "Bachata",    "bachata éxitos"),
            ("merengue",   "Merengue",   "merengue éxitos"),
            ("reggaeton",  "Reggaetón",  "reggaeton éxitos 2024"),
            ("rock",       "Rock",       "rock en español éxitos"),
            ("balada",     "Baladas",    "baladas románticas mix"),
            ("reggae",     "Reggae",     "reggae éxitos"),
            ("pop",        "Pop",        "pop latino éxitos"),
            ("cumbia",     "Cumbia",     "cumbia éxitos"),
            ("vallenato",  "Vallenato",  "vallenato éxitos"),
            ("ranchera",   "Ranchera",   "rancheras éxitos"),
            ("corrido",    "Corridos",   "corridos tumbados mix"),
            ("rap",        "Rap / Hip-Hop", "rap éxitos"),
            ("trap",       "Trap",       "trap latino mix"),
        };

        // ══════════════════════════════════════════════
        //  Conexión — aceptar Android Auto y todos
        // ══════════════════════════════════════════════

        public MediaSession.ConnectionResult OnConnect(
            MediaSession session,
            MediaSession.ControllerInfo controller)
        {
            // Player.COMMAND_* correctos de Media3 Player.java
            var playerCommands = MediaSession.ConnectionResult.DefaultPlayerCommands
                .BuildUpon()
                .Add(1)  // PLAY_PAUSE
                .Add(2)  // PREPARE
                .Add(3)  // STOP
                .Add(4)  // SEEK_TO_DEFAULT_POSITION
                .Add(5)  // SEEK_IN_CURRENT_MEDIA_ITEM
                .Add(6)  // SEEK_TO_PREVIOUS_MEDIA_ITEM
                .Add(7)  // SEEK_TO_PREVIOUS
                .Add(8)  // SEEK_TO_NEXT_MEDIA_ITEM
                .Add(9)  // SEEK_TO_NEXT
                .Build();

            return MediaSession.ConnectionResult.Accept(
                MediaSession.ConnectionResult.DefaultSessionCommands,
                playerCommands);
        }

        public void OnPostConnect(MediaSession session, MediaSession.ControllerInfo controller) { }
        public void OnDisconnected(MediaSession session, MediaSession.ControllerInfo controller) { }
        public void OnPlayerInteractionFinished(MediaSession session, MediaSession.ControllerInfo controllerInfo, PlayerCommands playerCommands) { }

        public int OnPlayerCommandRequest(MediaSession session, MediaSession.ControllerInfo controller, int playerCommand)
            => playerCommand;

        public bool OnMediaButtonEvent(MediaSession session, MediaSession.ControllerInfo controllerInfo, Intent intent)
            => false;

        public IListenableFuture OnCustomCommand(MediaSession session, MediaSession.ControllerInfo controller, SessionCommand customCommand, Bundle args)
            => CreateImmediateFuture(new SessionResult(SessionResult.ResultErrorNotSupported));

        public IListenableFuture OnCustomCommand(MediaSession session, MediaSession.ControllerInfo controller, SessionCommand customCommand, Bundle args, MediaSession.IProgressReporter progressReporter)
            => CreateImmediateFuture(new SessionResult(SessionResult.ResultErrorNotSupported));

        public IListenableFuture OnSetRating(MediaSession session, MediaSession.ControllerInfo controller, Rating rating)
            => CreateImmediateFuture(new SessionResult(SessionResult.ResultErrorNotSupported));

        public IListenableFuture OnSetRating(MediaSession session, MediaSession.ControllerInfo controller, string mediaId, Rating rating)
            => CreateImmediateFuture(new SessionResult(SessionResult.ResultErrorNotSupported));

        // ══════════════════════════════════════════════
        //  Media Library — árbol navegable para Android Auto
        // ══════════════════════════════════════════════

        /// <summary>
        /// ╔══════════════════════════════════════════════════════════════════╗
        /// ║  OnGetLibraryRoot  (equivalente a onGetRoot en legacy)          ║
        /// ╚══════════════════════════════════════════════════════════════════╝
        ///
        /// Android Auto llama aquí PRIMERO al conectarse.
        /// Retorna el nodo raíz del árbol de medios.
        ///
        /// Reglas:
        ///   - DEBE ser Browsable (el coche necesita poder abrir la carpeta raíz)
        ///   - NO debe ser Playable (el root en sí no reproduce nada)
        ///   - El mediaId ("emusic_root") se pasará a OnGetChildren para pedir los hijos
        ///
        /// En legacy sería:
        ///   return new BrowserRoot("emusic_root", null);
        /// </summary>
        public IListenableFuture OnGetLibraryRoot(
            MLS.MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            MLS.LibraryParams? p)
        {
            var root = new MediaItem.Builder()
                .SetMediaId(ROOT_ID) // "emusic_root" — el coche usará este ID para pedir los hijos
                .SetMediaMetadata(new MediaMetadata.Builder()
                    // ┌─────────────────────────────────────────────────────┐
                    // │ FLAGS: equivalente a FLAG_BROWSABLE en legacy       │
                    // │ Solo browsable, NO playable → carpeta pura         │
                    // └─────────────────────────────────────────────────────┘
                    .SetIsBrowsable(Java.Lang.Boolean.True)   // = MediaBrowserCompat.MediaItem.FLAG_BROWSABLE
                    .SetIsPlayable(Java.Lang.Boolean.False)    // = NO FLAG_PLAYABLE
                    .SetMediaType(new Java.Lang.Integer((int)MediaMetadata.MediaTypeMusic))
                    .SetTitle("eMusicApp")
                    .Build())
                .Build();

            return CreateImmediateFuture(LibraryResult.OfItem(root, null));
        }

        /// <summary>
        /// ╔══════════════════════════════════════════════════════════════════╗
        /// ║  OnGetChildren  (equivalente a onLoadChildren en legacy)        ║
        /// ╚══════════════════════════════════════════════════════════════════╝
        ///
        /// El coche llama aquí cada vez que el usuario ABRE una carpeta.
        /// Recibe el parentId del nodo abierto y debe retornar sus hijos.
        ///
        /// En legacy sería:
        ///   override void onLoadChildren(string parentId, Result&lt;List&lt;MediaItem&gt;&gt; result)
        ///   {
        ///       result.detach();          // Para async
        ///       // ... buscar en API ...
        ///       result.sendResult(list);  // Devolver hijos
        ///   }
        ///
        /// En Media3 retornamos un IListenableFuture que puede ser:
        ///   - Inmediato (datos estáticos como la lista de géneros)
        ///   - Async (datos que vienen de la API/Raspberry Pi)
        ///
        /// Estructura del switch por parentId:
        ///
        ///   parentId              │ Retorna
        ///   ──────────────────────┼────────────────────────────
        ///   "emusic_root"         │ [Géneros, Tendencias]           ← estático
        ///   "emusic_genres"       │ [Salsa, Rock, Bachata, ...]     ← estático
        ///   "genre:salsa"         │ [Track1, Track2, ...]           ← ASYNC (API)
        ///   "genre:rock"          │ [Track1, Track2, ...]           ← ASYNC (API)
        ///   "emusic_trending"     │ [Track1, Track2, ...]           ← ASYNC (API)
        /// </summary>
        public IListenableFuture OnGetChildren(
            MLS.MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            string parentId, int page, int pageSize,
            MLS.LibraryParams? p)
        {
            // ════════════════════════════════════════════════
            //  CASO 1: parentId = "emusic_root"
            //  El coche acaba de abrir la app. Mostrar las categorías principales.
            //  Datos estáticos → retorno inmediato (no necesita API).
            // ════════════════════════════════════════════════
            if (parentId == ROOT_ID)
            {
                var children = new List<MediaItem>
                {
                    // Carpetas puras: Browsable=true, Playable=false
                    // En legacy: new MediaItem(description, FLAG_BROWSABLE)
                    BuildFolderItem(GENRES_ID, "Géneros", "Explora por género musical"),
                    BuildFolderItem(TRENDING_ID, "Tendencias", "Lo más popular ahora"),
                };
                return CreateImmediateFuture(LibraryResult.OfItemList(children, null));
            }

            // ════════════════════════════════════════════════
            //  CASO 2: parentId = "emusic_genres"
            //  El usuario abrió la carpeta "Géneros". Mostrar cada género.
            //  Datos estáticos → retorno inmediato.
            //
            //  Cada género es Browsable + Playable (AMBOS flags combinados):
            //    - Si el usuario ABRE la carpeta → OnGetChildren("genre:salsa") → tracks
            //    - Si el usuario toca PLAY → OnSetMediaItems("genre:salsa") → reproduce mix
            //
            //  En legacy: new MediaItem(desc, FLAG_BROWSABLE | FLAG_PLAYABLE)
            //  En Media3: SetIsBrowsable(true) + SetIsPlayable(true)
            // ════════════════════════════════════════════════
            if (parentId == GENRES_ID)
            {
                var children = new List<MediaItem>();
                foreach (var g in Genres)
                {
                    children.Add(BuildGenreItem(g.id, g.name));
                }
                return CreateImmediateFuture(LibraryResult.OfItemList(children, null));
            }

            // ════════════════════════════════════════════════
            //  CASO 3: parentId = "genre:salsa" (o cualquier "genre:*")
            //  El usuario abrió un género. Buscar tracks en la API.
            //
            //  ⚡ AQUÍ VA LA LLAMADA ASYNC A TU API / RASPBERRY PI ⚡
            //  SearchTracksAsync() hace GET a:
            //    http://emusicmp3.duckdns.org:5050/api/search?q=salsa+éxitos
            //
            //  Retorna tracks con Playable=true, Browsable=false.
            //  En legacy: new MediaItem(desc, FLAG_PLAYABLE)
            //
            //  Usamos CreateAsyncFuture porque la API tarda ~5-7 segundos.
            //  Android Auto muestra un spinner mientras espera.
            // ════════════════════════════════════════════════
            if (parentId.StartsWith(GENRE_PREFIX))
            {
                var genreId = parentId.Substring(GENRE_PREFIX.Length);
                var genre = Genres.FirstOrDefault(g => g.id == genreId);
                var query = genre.query ?? $"{genreId} éxitos";

                return CreateAsyncFuture(async () =>
                {
                    // ── Llamada async a la API en la Raspberry Pi ──
                    var tracks = await SearchTracksAsync(query);
                    var items = new List<MediaItem>();
                    foreach (var t in tracks.Take(20))
                        items.Add(BuildPlayableItem(t.videoId, t.title, t.artist, t.thumb));
                    return (Java.Lang.Object)LibraryResult.OfItemList(items, null);
                });
            }

            // ════════════════════════════════════════════════
            //  CASO 4: parentId = "emusic_trending"
            //  Buscar tendencias en la API.
            //
            //  ⚡ LLAMADA ASYNC A TU API / RASPBERRY PI ⚡
            //  Mismo patrón que los géneros.
            // ════════════════════════════════════════════════
            if (parentId == TRENDING_ID)
            {
                return CreateAsyncFuture(async () =>
                {
                    // ── Llamada async a la API en la Raspberry Pi ──
                    var tracks = await SearchTracksAsync("música popular éxitos 2024");
                    var items = new List<MediaItem>();
                    foreach (var t in tracks.Take(20))
                        items.Add(BuildPlayableItem(t.videoId, t.title, t.artist, t.thumb));
                    return (Java.Lang.Object)LibraryResult.OfItemList(items, null);
                });
            }

            // Nodo desconocido → lista vacía
            return CreateImmediateFuture(LibraryResult.OfItemList(new List<MediaItem>(), null));
        }

        public IListenableFuture OnGetItem(
            MLS.MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            string mediaId)
        {
            // Géneros
            if (mediaId.StartsWith(GENRE_PREFIX))
            {
                var genreId = mediaId.Substring(GENRE_PREFIX.Length);
                var genre = Genres.FirstOrDefault(g => g.id == genreId);
                if (genre.id != null)
                {
                    var item = BuildGenreItem(genre.id, genre.name);
                    return CreateImmediateFuture(LibraryResult.OfItem(item, null));
                }
            }

            // Carpetas conocidas
            if (mediaId == GENRES_ID)
                return CreateImmediateFuture(LibraryResult.OfItem(
                    BuildFolderItem(GENRES_ID, "Géneros", "Explora por género musical"), null));
            if (mediaId == TRENDING_ID)
                return CreateImmediateFuture(LibraryResult.OfItem(
                    BuildFolderItem(TRENDING_ID, "Tendencias", "Lo más popular ahora"), null));

            return CreateImmediateFuture(LibraryResult.OfError(LibraryResult.ResultErrorNotSupported));
        }

        // ══════════════════════════════════════════════
        //  Búsqueda desde Android Auto
        // ══════════════════════════════════════════════

        public IListenableFuture OnGetSearchResult(
            MLS.MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            string query, int page, int pageSize,
            MLS.LibraryParams? p)
        {
            return CreateAsyncFuture(async () =>
            {
                var tracks = await SearchTracksAsync(query);
                var items = new List<MediaItem>();
                foreach (var t in tracks.Take(10))
                    items.Add(BuildPlayableItem(t.videoId, t.title, t.artist, t.thumb));
                return (Java.Lang.Object)LibraryResult.OfItemList(items, null);
            });
        }

        public IListenableFuture OnSearch(
            MLS.MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            string query,
            MLS.LibraryParams? p)
        {
            System.Diagnostics.Debug.WriteLine($"[LibraryCallback] OnSearch: '{query}'");
            Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(100);
                    session.NotifySearchResultChanged(browser, query, 0, null);
                }
                catch { }
            });
            return CreateImmediateFuture(LibraryResult.OfVoid());
        }

        public IListenableFuture OnSubscribe(
            MLS.MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            string parentId, MLS.LibraryParams? p)
            => CreateImmediateFuture(LibraryResult.OfVoid());

        public IListenableFuture OnUnsubscribe(
            MLS.MediaLibrarySession session,
            MediaSession.ControllerInfo browser,
            string parentId)
            => CreateImmediateFuture(LibraryResult.OfVoid());

        // ══════════════════════════════════════════════
        //  Voice + Play: resolución de items
        // ══════════════════════════════════════════════

        /// <summary>
        /// ╔══════════════════════════════════════════════════════════════════╗
        /// ║  OnSetMediaItems — Callbacks del coche a la app                 ║
        /// ╚══════════════════════════════════════════════════════════════════╝
        ///
        /// En legacy, las órdenes del coche llegaban a MediaSession.Callback:
        ///   onPlayFromMediaId(mediaId)   → usuario tocó un item del árbol
        ///   onPlayFromSearch(query)       → usuario usó voz: "reproduce salsa"
        ///   onPlay/onPause/onSkipToNext   → botones físicos del volante
        ///
        /// En Media3, TODO pasa por OnSetMediaItems/OnAddMediaItems.
        /// Los botones físicos (play/pause/next) van directo a ExoPlayer
        /// vía SkipAwareForwardingPlayer (AndroidMedia3Service.cs).
        ///
        /// ⚡ Cadena de resolución async (llamada a API / Raspberry Pi):
        ///
        ///   1. mediaId = "genre:salsa"
        ///      → SearchTracksAsync("salsa éxitos")     // GET /api/search?q=...
        ///      → GetBestStreamUrlAsync(videoId)         // GET /api/streams/{id}
        ///      → BuildResolvedItem con URI de audio
        ///
        ///   2. mediaId = videoId (ej: "dQw4w9WgXcQ")
        ///      → ResolveStreamAsync(videoId)            // GET /api/streams/{id}
        ///      → BuildResolvedItem con URI de audio
        ///
        ///   3. searchQuery presente (voz: "reproduce Bon Jovi")
        ///      → SearchAndResolveAsync("Bon Jovi")      // search + streams
        ///      → BuildResolvedItem con URI de audio
        /// </summary>
        public IListenableFuture OnSetMediaItems(
            MediaSession mediaSession,
            MediaSession.ControllerInfo controller,
            IList<MediaItem> mediaItems,
            int startIndex, long startPositionMs)
        {
            System.Diagnostics.Debug.WriteLine($"[LibraryCallback] OnSetMediaItems: {mediaItems.Count} items, startIndex={startIndex}");
            return CreateAsyncFuture(async () =>
            {
                var resolved = await ResolveItemsInternalAsync(mediaItems);
                System.Diagnostics.Debug.WriteLine($"[LibraryCallback] Resolved {resolved.Count} items");
                return (Java.Lang.Object)new MediaSession.MediaItemsWithStartPosition(
                    resolved, startIndex, startPositionMs);
            });
        }

        public IListenableFuture OnAddMediaItems(
            MediaSession session,
            MediaSession.ControllerInfo controller,
            IList<MediaItem> mediaItems)
        {
            return CreateAsyncFuture(async () =>
            {
                var resolved = await ResolveItemsInternalAsync(mediaItems);
                var javaList = new Java.Util.ArrayList();
                foreach (var r in resolved) javaList.Add(r);
                return (Java.Lang.Object)javaList;
            });
        }

        // ══════════════════════════════════════════════
        //  Playback resumption
        // ══════════════════════════════════════════════

        public IListenableFuture OnPlaybackResumption(MediaSession mediaSession, MediaSession.ControllerInfo controller)
        {
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
                System.Diagnostics.Debug.WriteLine($"[LibraryCallback] Error reading player state: {ex.Message}");
            }

            if (currentItem != null)
            {
                var list = new List<MediaItem> { currentItem };
                return CreateImmediateFuture(new MediaSession.MediaItemsWithStartPosition(
                    list, currentIndex, currentPosition));
            }

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

        // ══════════════════════════════════════════════
        //  Resolución interna de items
        // ══════════════════════════════════════════════

        private async Task<List<MediaItem>> ResolveItemsInternalAsync(IList<MediaItem> mediaItems)
        {
            var resolved = new List<MediaItem>();

            foreach (var item in mediaItems)
            {
                // 1. ¿Es un género? (mediaId = "genre:salsa")
                if (item.MediaId?.StartsWith(GENRE_PREFIX) == true)
                {
                    var genreId = item.MediaId.Substring(GENRE_PREFIX.Length);
                    var genre = Genres.FirstOrDefault(g => g.id == genreId);
                    var query = genre.query ?? $"{genreId} éxitos";
                    var track = await SearchAndResolveAsync(query);
                    if (track != null)
                    {
                        resolved.Add(BuildResolvedItem(
                            track.Value.videoId, track.Value.title,
                            track.Value.artist, track.Value.thumb, track.Value.streamUrl));
                    }
                    continue;
                }

                // 2. ¿Tiene searchQuery? (comando de voz)
                var searchQuery = GetSearchQuery(item);
                System.Diagnostics.Debug.WriteLine($"[LibraryCallback] Resolving query='{searchQuery}' mediaId='{item.MediaId}'");

                if (!string.IsNullOrEmpty(searchQuery))
                {
                    var track = await SearchAndResolveAsync(searchQuery);
                    if (track != null)
                    {
                        resolved.Add(BuildResolvedItem(
                            track.Value.videoId, track.Value.title, track.Value.artist,
                            track.Value.thumb, track.Value.streamUrl));
                    }
                }
                // 3. ¿Tiene mediaId que parece videoId?
                else if (!string.IsNullOrEmpty(item.MediaId) && !item.MediaId.StartsWith("emusic_"))
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
                // 4. Fallback: título como búsqueda
                else
                {
                    var title = item.MediaMetadata?.Title?.ToString();
                    if (!string.IsNullOrEmpty(title))
                    {
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

            // Fallback global: si no se resolvió nada, reproducir algo genérico
            if (resolved.Count == 0 && mediaItems.Count > 0)
            {
                var fallback = await SearchAndResolveAsync("música popular");
                if (fallback != null)
                {
                    resolved.Add(BuildResolvedItem(
                        fallback.Value.videoId, fallback.Value.title,
                        fallback.Value.artist, fallback.Value.thumb, fallback.Value.streamUrl));
                }
            }

            return resolved;
        }

        private static string? GetSearchQuery(MediaItem item)
        {
            try
            {
                var itemClass = Java.Lang.Class.FromType(typeof(MediaItem));
                var field = itemClass.GetField("requestMetadata");
                var reqMeta = field?.Get(item);
                if (reqMeta != null)
                {
                    var searchField = reqMeta.Class.GetField("searchQuery");
                    var searchQuery = searchField?.Get(reqMeta)?.ToString();
                    if (!string.IsNullOrEmpty(searchQuery))
                        return searchQuery;
                }
            }
            catch { }

            try
            {
                var title = item.MediaMetadata?.Title?.ToString();
                if (!string.IsNullOrEmpty(title)) return title;
            }
            catch { }

            try
            {
                var mediaId = item.MediaId;
                if (!string.IsNullOrEmpty(mediaId) && mediaId.Length > 15) return mediaId;
            }
            catch { }

            return null;
        }

        // ══════════════════════════════════════════════
        //  Future helpers
        // ══════════════════════════════════════════════

        private static IListenableFuture CreateImmediateFuture(Java.Lang.Object result)
            => CallbackToFutureAdapter.GetFuture(new ImmediateResolver(result));

        private static IListenableFuture CreateAsyncFuture(System.Func<Task<Java.Lang.Object>> asyncFunc)
            => CallbackToFutureAdapter.GetFuture(new AsyncResolver(asyncFunc));

        private class ImmediateResolver : Java.Lang.Object, CallbackToFutureAdapter.IResolver
        {
            private readonly Java.Lang.Object _result;
            public ImmediateResolver(Java.Lang.Object result) => _result = result;
            public Java.Lang.Object? AttachCompleter(CallbackToFutureAdapter.Completer completer)
            { completer.Set(_result); return null; }
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
                        using var cts = new System.Threading.CancellationTokenSource(System.TimeSpan.FromSeconds(30));
                        var task = _func();
                        var completed = await Task.WhenAny(task, Task.Delay(-1, cts.Token));
                        if (completed == task)
                        {
                            completer.Set(await task);
                        }
                        else
                        {
                            System.Diagnostics.Debug.WriteLine("[LibraryCallback] Timeout: resolución tardó >30s");
                            completer.Set(new Java.Util.ArrayList());
                        }
                    }
                    catch (System.Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine($"[LibraryCallback] Error: {ex.Message}\n{ex.StackTrace}");
                        completer.Set(new Java.Util.ArrayList());
                    }
                });
                return null;
            }
        }

        // ══════════════════════════════════════════════
        //  API calls
        // ══════════════════════════════════════════════

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
                var json = await _http.GetStringAsync(url);
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
            catch (System.Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LibraryCallback] Search error: {ex.Message}");
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
                string? bestUrl = null; int bestBitrate = 0;
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
                string? bestUrl = null; int bestBitrate = 0;
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

        // ══════════════════════════════════════════════
        //  MediaItem builders
        // ══════════════════════════════════════════════

        // ╔══════════════════════════════════════════════════════════════════╗
        // ║  MediaItem Builders — Sistema de Flags                           ║
        // ║                                                                   ║
        // ║  En Media3, los "flags" de MediaBrowserCompat.MediaItem se       ║
        // ║  expresan como propiedades booleanas en MediaMetadata:            ║
        // ║                                                                   ║
        // ║  Legacy (deprecado)               │ Media3 (actual)              ║
        // ║  ─────────────────────────────────┼──────────────────────────── ║
        // ║  FLAG_BROWSABLE                   │ SetIsBrowsable(true)         ║
        // ║  FLAG_PLAYABLE                    │ SetIsPlayable(true)          ║
        // ║  FLAG_BROWSABLE | FLAG_PLAYABLE   │ SetIsBrowsable(true)         ║
        // ║                                   │ + SetIsPlayable(true)        ║
        // ║                                                                   ║
        // ║  Resultado en la pantalla del coche:                             ║
        // ║    Solo Browsable → icono de carpeta 📂                          ║
        // ║    Solo Playable  → icono de canción 🎵 con botón play          ║
        // ║    Ambos          → carpeta 📂 con botón play superpuesto       ║
        // ╚══════════════════════════════════════════════════════════════════╝

        /// <summary>
        /// SOLO BROWSABLE (carpeta pura).
        /// En legacy: new MediaItem(description, FLAG_BROWSABLE)
        ///
        /// Uso: "Géneros", "Tendencias" — carpetas que NO se pueden reproducir,
        /// solo se pueden abrir para ver su contenido.
        /// </summary>
        private static MediaItem BuildFolderItem(string mediaId, string title, string subtitle)
        {
            var meta = new MediaMetadata.Builder()
                .SetTitle(title)
                .SetArtist(subtitle)
                .SetIsBrowsable(Java.Lang.Boolean.True)    // FLAG_BROWSABLE → carpeta
                .SetIsPlayable(Java.Lang.Boolean.False)     // NO playable
                .SetMediaType(new Java.Lang.Integer((int)MediaMetadata.MediaTypeFolderMixed))
                .Build();
            return new MediaItem.Builder()
                .SetMediaId(mediaId)
                .SetMediaMetadata(meta)
                .Build();
        }

        /// <summary>
        /// BROWSABLE + PLAYABLE (carpeta con botón play).
        /// En legacy: new MediaItem(description, FLAG_BROWSABLE | FLAG_PLAYABLE)
        ///
        /// Uso: "Salsa", "Rock" — el usuario puede:
        ///   - ABRIR la carpeta → OnGetChildren("genre:salsa") → ver tracks
        ///   - TOCAR PLAY → OnSetMediaItems("genre:salsa") → reproducir mix
        ///
        /// El mediaId usa prefijo "genre:" para distinguir géneros de videoIds.
        /// </summary>
        private static MediaItem BuildGenreItem(string genreId, string displayName)
        {
            var meta = new MediaMetadata.Builder()
                .SetTitle(displayName)
                .SetIsBrowsable(Java.Lang.Boolean.True)    // FLAG_BROWSABLE → se puede abrir
                .SetIsPlayable(Java.Lang.Boolean.True)      // FLAG_PLAYABLE → se puede reproducir
                .SetMediaType(new Java.Lang.Integer((int)MediaMetadata.MediaTypeMusic))
                .Build();
            return new MediaItem.Builder()
                .SetMediaId(GENRE_PREFIX + genreId)  // "genre:salsa" — prefijo para distinguir
                .SetMediaMetadata(meta)
                .Build();
        }

        /// <summary>
        /// SOLO PLAYABLE (canción).
        /// En legacy: new MediaItem(description, FLAG_PLAYABLE)
        ///
        /// Uso: tracks individuales dentro de un género o tendencias.
        /// El mediaId es el videoId real. Cuando el usuario toca play,
        /// OnSetMediaItems recibe este item y ResolveItemsInternalAsync
        /// llama a la API para obtener la URL de stream.
        ///
        /// ⚡ NOTA: Este item NO tiene URI de stream todavía.
        /// La resolución async (llamada a /streams/{videoId} en la Raspberry Pi)
        /// ocurre DESPUÉS, cuando el usuario da play, en OnSetMediaItems.
        /// </summary>
        private static MediaItem BuildPlayableItem(string videoId, string title, string artist, string thumbUrl)
        {
            var meta = new MediaMetadata.Builder()
                .SetTitle(title)
                .SetArtist(artist)
                .SetMediaType(new Java.Lang.Integer((int)MediaMetadata.MediaTypeMusic))
                .SetIsPlayable(Java.Lang.Boolean.True)      // FLAG_PLAYABLE → canción
                .SetIsBrowsable(Java.Lang.Boolean.False);    // NO browsable → no es carpeta
            if (!string.IsNullOrEmpty(thumbUrl))
                meta.SetArtworkUri(global::Android.Net.Uri.Parse(thumbUrl));
            return new MediaItem.Builder()
                .SetMediaId(videoId)  // videoId real — se usará para resolver el stream
                .SetMediaMetadata(meta.Build())
                .Build();
        }

        /// <summary>
        /// PLAYABLE CON STREAM RESUELTO (listo para ExoPlayer).
        /// Este item YA tiene la URL de audio (.SetUri) después de llamar a la API.
        ///
        /// Se construye en ResolveItemsInternalAsync después de:
        ///   1. Buscar en API: GET /search?q=...
        ///   2. Resolver stream: GET /streams/{videoId}
        ///   3. Seleccionar mejor bitrate de audioStreams[]
        ///
        /// ExoPlayer recibe este MediaItem con URI y empieza a reproducir.
        /// </summary>
        private static MediaItem BuildResolvedItem(string videoId, string title, string artist, string thumbUrl, string streamUrl)
        {
            var meta = new MediaMetadata.Builder()
                .SetTitle(title)
                .SetArtist(artist)
                .SetMediaType(new Java.Lang.Integer((int)MediaMetadata.MediaTypeMusic))
                .SetIsPlayable(Java.Lang.Boolean.True);
            if (!string.IsNullOrEmpty(thumbUrl))
                meta.SetArtworkUri(global::Android.Net.Uri.Parse(thumbUrl));
            return new MediaItem.Builder()
                .SetMediaId(videoId)
                .SetUri(streamUrl)   // ← URL real del audio, ExoPlayer la reproduce directamente
                .SetMediaMetadata(meta.Build())
                .Build();
        }
    }
}
