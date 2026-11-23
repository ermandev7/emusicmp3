using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using eMusicApi.Models;

namespace eMusicApi.Services;

public class UserProfile
{
    public Dictionary<string, double> TokenVector { get; init; } = new();
    public List<KeyValuePair<string, double>> TopArtists { get; init; } = new();
    public List<KeyValuePair<string, double>> TopGenres { get; init; } = new();
    public HashSet<string> PlayedVideoIds { get; init; } = new();
    /// <summary>Artistas marcados como favoritos (señal explícita).</summary>
    public HashSet<string> FavoriteArtists { get; init; } = new();

    /// <summary>
    /// Peso del token más fuerte del perfil. Se usa para normalizar la afinidad de
    /// contenido a un rango 0..1 estable, sin importar cuánto historial tenga el usuario.
    /// </summary>
    public double MaxTokenWeight { get; init; } = 1.0;

    /// <summary>
    /// Artistas que el usuario saltea sistemáticamente, con su proporción de skips
    /// (0..1). Señal negativa: no alcanza con no premiarlos, hay que penalizarlos.
    /// </summary>
    public Dictionary<string, double> SkippedArtists { get; init; } = new();

    /// <summary>
    /// Todos los artistas que el usuario ya conoce (historial + favoritos, normalizados).
    /// Sirve para distinguir "más de lo que ya escucha" de "descubrimiento".
    /// </summary>
    public HashSet<string> KnownArtists { get; init; } = new();

    /// <summary>
    /// Canciones que el usuario saltó (la última vez que sonaron, o repetidamente).
    /// No se vuelven a recomendar aunque haya pasado la ventana de "escuchada hace poco".
    /// </summary>
    public HashSet<string> SkippedVideoIds { get; init; } = new();
}

/// <summary>Canción de partida para buscar parecidas, con su peso en el gusto del usuario.</summary>
public sealed record Seed(string VideoId, string Artist, double Weight);

/// <summary>Candidato a recomendación, venga de una búsqueda o de las parecidas a una semilla.</summary>
public sealed class Candidate
{
    public string VideoId { get; init; } = "";
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Thumb { get; init; } = "";
    public int Duration { get; init; }
    public string Url { get; init; } = "";

    /// <summary>
    /// Señal de descubrimiento sin normalizar: suma, por cada semilla cuya lista de
    /// parecidas contiene esta canción, del peso de la semilla atenuado por la posición.
    /// 0 = no la sugiere ninguna semilla (solo salió en una búsqueda).
    /// </summary>
    public double Discovery { get; set; }
}

public sealed record RankedCandidate(Candidate Candidate, double Score, bool IsNewArtist);

/// <summary>Candidato para un botón de género (un mix o una canción de ese género).</summary>
/// <param name="BestPosition">Mejor posición (0 = primero) en las búsquedas donde salió.</param>
public sealed record GenreCandidate(string VideoId, string Title, string Artist, int Duration, int BestPosition);

public sealed record RankedGenreCandidate(GenreCandidate Candidate, double Score, bool IsMix);

public class RecommendationEngine
{
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "official", "video", "audio", "lyrics", "lyric", "live", "hd", "4k",
        "ft", "feat", "featuring", "remix", "cover", "karaoke", "instrumental",
        "version", "remaster", "remastered", "explicit", "clean", "acoustic",
        "the", "of", "in", "on", "at", "to", "for", "and", "is", "it", "my",
        "your", "music", "song", "album", "full", "new", "best",
        "letra", "letras", "en", "vivo", "de", "la", "el", "los", "las",
        "del", "con", "para", "por", "una", "un", "que", "mi", "tu", "su",
        "y", "o", "a", "al", "se", "no", "me", "te", "lo", "le", "nos",
        "tema", "cancion", "musica", "exitos", "mix", "vol",
        // Palabras funcion que se colaban con peso alto en perfiles reales
        // (medido: "es" 69, "pa" 68, "yo" 66) y ensuciaban la afinidad.
        "es", "yo", "pa", "si", "ya", "muy", "mas", "más", "sin", "como",
        "cuando", "donde", "pero", "porque", "sobre", "entre", "hasta",
        "desde", "bb", "ah", "oh", "uh", "eh"
    };

    private static readonly Dictionary<string, string[]> GenrePatterns = new()
    {
        ["salsa"]      = new[] { "salsa" },
        ["bachata"]    = new[] { "bachata", "bachi" },
        ["reggaeton"]  = new[] { "reggaeton", "reggaetón", "perreo", "reggaet" },
        ["cumbia"]     = new[] { "cumbia", "cumbi" },
        ["merengue"]   = new[] { "merengue" },
        ["vallenato"]  = new[] { "vallenato" },
        ["rock"]       = new[] { "rock" },
        ["pop"]        = new[] { "pop" },
        ["rap"]        = new[] { "rap", "hip hop", "hip-hop" },
        ["trap"]       = new[] { "trap" },
        ["balada"]     = new[] { "balada", "romántic", "romantic" },
        ["ranchera"]   = new[] { "ranchera", "mariachi", "norteñ" },
        ["corrido"]    = new[] { "corrido", "tumbado" },
        ["electronic"] = new[] { "electronic", "edm", "house", "techno" },
        ["jazz"]       = new[] { "jazz" },
        ["blues"]      = new[] { "blues" },
        ["reggae"]     = new[] { "reggae" },
        ["clasica"]    = new[] { "classical", "clásica" },
        ["kpop"]       = new[] { "kpop", "k-pop" },
        ["r&b"]        = new[] { "r&b", "rnb", "soul" },
    };

    // Plantillas de búsqueda por género. "{year}" se sustituye por el año actual al
    // vuelo (ver GenerateSearchQueries): antes llevaban "2024" fijo en el texto, así
    // que dos años después seguían sesgando la búsqueda hacia música vieja en vez de
    // lo más reciente, y el problema solo iba a más con cada año que pasara.
    private static readonly Dictionary<string, string[]> GenreSearchQueries = new()
    {
        ["salsa"]      = new[] { "salsa éxitos", "salsa romántica mix", "salsa clásica", "lo mejor de la salsa" },
        ["bachata"]    = new[] { "bachata éxitos", "bachata romántica", "bachata sensual mix" },
        ["reggaeton"]  = new[] { "reggaeton éxitos {year}", "reggaeton mix", "perreo mix" },
        ["cumbia"]     = new[] { "cumbia éxitos", "cumbia mix bailable", "cumbia clásica" },
        ["merengue"]   = new[] { "merengue éxitos", "merengue mix bailable" },
        ["vallenato"]  = new[] { "vallenato éxitos", "vallenato romántico mix" },
        ["rock"]       = new[] { "rock en español éxitos", "rock clásico mix", "rock latino" },
        ["pop"]        = new[] { "pop éxitos {year}", "pop latino mix", "pop en español" },
        ["rap"]        = new[] { "rap éxitos", "hip hop mix", "rap en español mix" },
        ["trap"]       = new[] { "trap latino mix", "trap éxitos {year}" },
        ["balada"]     = new[] { "baladas románticas mix", "baladas en español" },
        ["ranchera"]   = new[] { "rancheras éxitos", "música mexicana mix", "mariachi éxitos" },
        ["corrido"]    = new[] { "corridos tumbados mix", "corridos éxitos {year}" },
        ["electronic"] = new[] { "electronic dance mix", "EDM mix {year}" },
        ["jazz"]       = new[] { "jazz clásico", "smooth jazz mix" },
        ["blues"]      = new[] { "blues éxitos", "blues clásico mix" },
        ["reggae"]     = new[] { "reggae éxitos", "reggae mix" },
        ["clasica"]    = new[] { "música clásica famosa", "piano clásico" },
        ["kpop"]       = new[] { "kpop éxitos {year}", "kpop mix" },
        ["r&b"]        = new[] { "r&b éxitos", "soul music mix" },
    };

    // ──────────────── Tokenización ────────────────

    public static string[] Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Array.Empty<string>();
        text = Regex.Replace(text, @"\([^)]*\)", " ");
        text = Regex.Replace(text, @"\[[^\]]*\]", " ");
        return Regex.Split(text.ToLowerInvariant(), @"[^a-záéíóúñü\w]+")
            .Where(w => w.Length > 1 && !StopWords.Contains(w))
            .ToArray();
    }

    public static string NormalizeArtist(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return "";
        var a = artist.Trim().ToLowerInvariant();
        a = Regex.Replace(a, @"\s*-\s*topic$", "");
        return a;
    }

    public static string? DetectGenre(string title, string artist)
    {
        var combined = $"{title} {artist}".ToLowerInvariant();
        foreach (var (genre, patterns) in GenrePatterns)
        {
            foreach (var p in patterns)
                if (combined.Contains(p)) return genre;
        }
        // Antes, cualquier título con "amor" o "corazón" contaba como "balada". Medido en
        // un perfil real, eso convertía "balada" en el 2º género de alguien que escucha
        // sobre todo reggaetón, rock y cumbia, y metía "baladas románticas mix" entre
        // sus búsquedas. Si el título no nombra un género, no se inventa uno.
        return null;
    }

    // ──────────────── Perfil del usuario ────────────────
    //
    //  W(i) = PlayCount × e^(-0.05 × days) × SkipPenalty × DownloadBonus
    //  TF(t) = Σ W(i)  para tracks que contienen token t
    //  IDF(t) = ln((N+1)/(df+1)) + 1
    //  ProfileVector[t] = TF(t) × IDF(t)

    // Peso de un favorito (corazón). Señal explícita fuerte, sin decay temporal:
    // equivale a una canción bastante escuchada.
    private const double FavoriteWeight = 8.0;

    // Antes una canción escuchada UNA VEZ quedaba excluida de "siguiente canción"
    // para siempre. Si un artista favorito solo tiene pocas canciones encontrables
    // y ya sonaron todas, el radio se quedaba sin nada de ese artista para siempre,
    // aunque sea justo lo que más le gusta al usuario. Pasada esta ventana, la
    // canción vuelve a ser candidata — sigue evitando repetir lo que sonó hace
    // poco, pero ya no la destierra de por vida.
    private const double RecentExclusionWindowDays = 21.0;

    // Olvido del historial: el peso de una escucha se reduce a la mitad cada 45 días.
    // Antes era cada 14 (e^(-0.05·días)): medido en un perfil real, 118 de 200 canciones
    // tenían más de 60 días y pesaban menos del 5 %, así que el perfil reflejaba solo
    // las dos últimas semanas y cambiaba de golpe con cada racha de escucha.
    private const double RecencyDecayPerDay = 0.0154; // ln(2) / 45

    /// <summary>Cuánto "vota" una entrada del historial por el gusto del usuario.</summary>
    private static double HistoryWeight(History entry, DateTime now)
    {
        double daysSince = Math.Max(0, (now - entry.PlayedAt).TotalDays);
        double recency = Math.Exp(-RecencyDecayPerDay * daysSince);
        // Un tema cuya última escucha fue un salto apenas aporta (0.1). Si después se
        // volvió a escuchar entero, cuenta, pero menos cuantas más veces se saltó.
        double skipRatio = Math.Min(1.0, SkipEvents(entry) / (double)Math.Max(1, entry.PlayCount));
        double skipPenalty = entry.SkippedEarly ? 0.1 : 1.0 - 0.7 * skipRatio;
        double downloadBonus = entry.IsDownloaded ? 1.5 : 1.0;
        return entry.PlayCount * recency * skipPenalty * downloadBonus;
    }

    /// <summary>
    /// Veces que se saltó la canción. Las filas anteriores a la columna SkipCount solo
    /// tienen el indicador SkippedEarly, que cuenta como un salto.
    /// </summary>
    private static int SkipEvents(History entry) => Math.Max(entry.SkipCount, entry.SkippedEarly ? 1 : 0);

    /// <param name="excludedArtists">
    /// Artistas marcados "no recomendar" (normalizados). Se quitan del perfil: medido en
    /// un perfil real, los 3 artistas más escuchados estaban excluidos y seguían
    /// dominando top artistas, género y búsquedas ("música similar a X"), así que se
    /// recomendaba "más de lo mismo" que el usuario había rechazado.
    /// </param>
    public UserProfile BuildProfile(List<History> history, List<Favorite>? favorites = null,
        ISet<string>? excludedArtists = null)
    {
        favorites ??= new List<Favorite>();
        if (excludedArtists is { Count: > 0 })
        {
            bool IsExcluded(string artist)
            {
                var na = NormalizeArtist(artist);
                return na.Length > 0 && excludedArtists.Any(ex => ArtistMatches(ex, na));
            }
            history = history.Where(h => !IsExcluded(h.Artist)).ToList();
            favorites = favorites.Where(f => !IsExcluded(f.Artist)).ToList();
        }
        if (history.Count == 0 && favorites.Count == 0)
            return new UserProfile();

        var tokenFreq = new Dictionary<string, double>();
        var tokenDocFreq = new Dictionary<string, int>();
        var artistWeights = new Dictionary<string, double>();
        var genreWeights = new Dictionary<string, double>();
        var playedIds = new HashSet<string>();
        var favoriteArtists = new HashSet<string>();
        var knownArtists = new HashSet<string>();
        var skippedIds = new HashSet<string>();
        // Reproducciones y skips por artista, para la señal negativa.
        var artistPlays = new Dictionary<string, int>();
        var artistSkips = new Dictionary<string, int>();
        var now = DateTime.UtcNow;

        // Acumula una "canción" (del historial o favorita) en los vectores del perfil.
        // Ya NO decide aquí qué se excluye de "siguiente canción": eso se hace más
        // abajo, con ventana de tiempo para el historial y nunca para favoritos
        // (ver comentarios en los bucles).
        void Accumulate(string title, string artist, double weight)
        {
            // Se tokeniza título Y artista. Antes solo el título, y tras quitar stopwords
            // los títulos quedan casi en ruido ("una", "mas"); el nombre del artista es
            // con diferencia la palabra más informativa de una canción.
            var tokens = Tokenize($"{title} {artist}");
            foreach (var token in tokens)
            {
                tokenFreq.TryGetValue(token, out double cur);
                tokenFreq[token] = cur + weight;
            }
            foreach (var token in new HashSet<string>(tokens))
            {
                tokenDocFreq.TryGetValue(token, out int df);
                tokenDocFreq[token] = df + 1;
            }

            var normArtist = NormalizeArtist(artist);
            if (normArtist.Length > 0)
            {
                artistWeights.TryGetValue(normArtist, out double cur);
                artistWeights[normArtist] = cur + weight;
            }

            var genre = DetectGenre(title, artist);
            if (genre != null)
            {
                genreWeights.TryGetValue(genre, out double cur);
                genreWeights[genre] = cur + weight;
            }
        }

        foreach (var entry in history)
        {
            double daysSince = Math.Max(0, (now - entry.PlayedAt).TotalDays);
            Accumulate(entry.Title, entry.Artist, HistoryWeight(entry, now));

            // Canción saltada la última vez, o saltada al menos la mitad de las veces
            // (mínimo 2): no se vuelve a recomendar, haya pasado el tiempo que haya.
            int skips = SkipEvents(entry);
            if (!string.IsNullOrEmpty(entry.VideoId) &&
                (entry.SkippedEarly || (skips >= 2 && skips * 2 >= entry.PlayCount)))
                skippedIds.Add(entry.VideoId);

            // Excluir de "siguiente canción" solo lo escuchado dentro de la ventana
            // reciente (evita repetir lo de hace un rato); pasada la ventana vuelve
            // a ser candidata.
            if (!string.IsNullOrEmpty(entry.VideoId) && daysSince <= RecentExclusionWindowDays)
                playedIds.Add(entry.VideoId);

            var artistKey = NormalizeArtist(entry.Artist);
            if (artistKey.Length > 0)
            {
                knownArtists.Add(artistKey);
                // Se cuentan escuchas y saltos reales, no filas: antes una canción
                // saltada cinco veces contaba como un único salto, y el indicador se
                // borraba en cuanto volvía a sonar.
                artistPlays[artistKey] = artistPlays.GetValueOrDefault(artistKey) + Math.Max(1, entry.PlayCount);
                if (skips > 0)
                    artistSkips[artistKey] = artistSkips.GetValueOrDefault(artistKey) + skips;
            }
        }

        // Favoritos: peso fuerte y constante. También registramos sus artistas.
        // A propósito NO se añaden a playedIds: es una señal positiva explícita, no
        // algo que el usuario quiera dejar de escuchar — el radio puede sugerir de
        // vez en cuando una canción que ya es favorita.
        foreach (var fav in favorites)
        {
            Accumulate(fav.Title, fav.Artist, FavoriteWeight);
            var na = NormalizeArtist(fav.Artist);
            if (na.Length > 0)
            {
                favoriteArtists.Add(na);
                knownArtists.Add(na);
            }
            // Un favorito nunca se da por "saltado": el corazón manda sobre un salto suelto.
            skippedIds.Remove(fav.Id);
        }

        int n = history.Count + favorites.Count;
        var profileVector = new Dictionary<string, double>();
        foreach (var (token, tf) in tokenFreq)
        {
            int df = tokenDocFreq.GetValueOrDefault(token, 1);
            double idf = Math.Log((double)(n + 1) / (df + 1)) + 1.0;
            profileVector[token] = tf * idf;
        }

        // Artistas con al menos 3 reproducciones y más de la mitad salteadas.
        // El umbral evita castigar a un artista por un único skip casual.
        var skippedArtists = new Dictionary<string, double>();
        foreach (var (artist, plays) in artistPlays)
        {
            if (plays < 3) continue;
            double ratio = Math.Min(1.0, artistSkips.GetValueOrDefault(artist) / (double)plays);
            if (ratio > 0.5) skippedArtists[artist] = ratio;
        }

        return new UserProfile
        {
            TokenVector = profileVector,
            TopArtists = artistWeights.OrderByDescending(kv => kv.Value).Take(5).ToList(),
            TopGenres = genreWeights.OrderByDescending(kv => kv.Value).Take(3).ToList(),
            PlayedVideoIds = playedIds,
            FavoriteArtists = favoriteArtists,
            MaxTokenWeight = profileVector.Count > 0 ? profileVector.Values.Max() : 1.0,
            SkippedArtists = skippedArtists,
            KnownArtists = knownArtists,
            SkippedVideoIds = skippedIds
        };
    }

    // ──────────────── Semillas para el descubrimiento ────────────────

    /// <summary>
    /// Elige las canciones de partida para pedir "parecidas a esta". Sorteo ponderado
    /// por el peso de cada canción en el gusto del usuario (favoritos y lo más/último
    /// escuchado salen más), sin repetir artista.
    ///
    /// Antes las semillas eran siempre las 3 primeras filas de Favoritos en el orden de
    /// la base de datos: con 57 favoritos, el descubrimiento partía eternamente de las
    /// mismas 3 canciones. Al sortear, cada recálculo explora una zona distinta del
    /// gusto del usuario.
    /// </summary>
    public List<Seed> PickSeeds(List<History> history, List<Favorite> favorites,
        ISet<string>? excludedArtists, int count, Random rng)
    {
        bool IsExcluded(string normArtist) =>
            excludedArtists is { Count: > 0 } && normArtist.Length > 0 &&
            excludedArtists.Any(ex => ArtistMatches(ex, normArtist));

        var now = DateTime.UtcNow;
        var pool = new Dictionary<string, Seed>();
        void Add(string videoId, string artist, double weight)
        {
            if (string.IsNullOrEmpty(videoId) || weight <= 0) return;
            var na = NormalizeArtist(artist);
            if (IsExcluded(na)) return;
            pool[videoId] = pool.TryGetValue(videoId, out var cur)
                ? cur with { Weight = cur.Weight + weight }
                : new Seed(videoId, na, weight);
        }

        foreach (var fav in favorites) Add(fav.Id, fav.Artist, FavoriteWeight);
        foreach (var entry in history)
        {
            if (entry.SkippedEarly) continue; // lo último que hizo con ella fue saltarla
            Add(entry.VideoId, entry.Artist, HistoryWeight(entry, now));
        }

        var remaining = pool.Values.ToList();
        var seeds = new List<Seed>();
        while (seeds.Count < count && remaining.Count > 0)
        {
            double pick = rng.NextDouble() * remaining.Sum(s => s.Weight);
            var chosen = remaining[^1];
            foreach (var s in remaining)
            {
                pick -= s.Weight;
                if (pick <= 0) { chosen = s; break; }
            }
            seeds.Add(chosen);
            // Una semilla por artista: seis canciones del mismo artista darían seis
            // listas de parecidas casi iguales.
            remaining.RemoveAll(s => s.VideoId == chosen.VideoId ||
                                     (chosen.Artist.Length > 0 && s.Artist == chosen.Artist));
        }
        return seeds;
    }

    // ──────────────── Scoring de candidatos ────────────────
    //
    //  Score = Contenido + ArtistBonus + GenreBonus + FavoriteBonus − SkipPenalty
    //
    //  Contenido    ∈ [0, 0.35]  afinidad media de las palabras del tema con el perfil
    //  ArtistBonus  ∈ [0, 0.30]  proporcional al peso del artista en el perfil
    //  FavoriteBonus  = 0.25     si el artista está entre los favoritos
    //  GenreBonus     = 0.20     si el género coincide con los top del usuario
    //  SkipPenalty  ∈ [0, 0.40]  si el usuario saltea sistemáticamente a ese artista
    //
    //  POR QUÉ NO HAY COSENO: la versión anterior dividía por la norma del vector de
    //  perfil COMPLETO, que crece con cada canción escuchada. Medido con perfiles
    //  simulados, el coseno daba una mediana de ~0.05 y su techo caía de 0.54 (20 temas
    //  de historial) a 0.21 (200 temas), mientras los bonus fijos suman hasta 0.75. O sea
    //  que el término "inteligente" quedaba anulado por los bonus, y encima empeoraba
    //  cuanto más usaba la app el usuario. Comparar un título de 3 palabras contra un
    //  perfil de 300 tokens con coseno siempre da valores diminutos: es inherente a la
    //  métrica cuando los vectores tienen tamaños tan distintos.
    //
    //  En su lugar se usa afinidad media por token, normalizada por el token más fuerte
    //  del perfil: da un valor estable en 0..1 sin importar el tamaño del historial.
    //  Se divide por sqrt(n) y no por n para premiar que coincidan VARIAS palabras sin
    //  que los títulos largos se diluyan.

    private const double ContentWeight = 0.35;
    private const double ArtistBonusMax = 0.30;
    private const double FavoriteBonusValue = 0.25;
    private const double GenreBonusValue = 0.20;
    private const double SkipPenaltyMax = 0.40;

    public double ScoreCandidate(UserProfile profile, string title, string artist)
    {
        // SOLO el titulo, no el artista. El perfil SI se construye con titulo+artista
        // (enriquece el vector), pero puntuar el candidato con el nombre del artista
        // lo contaba dos veces: una en ArtistBonus y otra en el contenido.
        //
        // Medido en un perfil real: "Don Omar - Dile" sacaba contenido 0.350 (el tope)
        // solo porque "don" y "omar" son tokens de peso ~95 en el perfil, mientras que
        // "La T y La M" sacaba 0.096 porque su nombre no deja ningun token ("la" es
        // stopword, "t" y "m" tienen una letra). Resultado: el artista #1 del usuario
        // quedaba por debajo del #2 y el #3, teniendo 2.4x mas peso de escucha.
        var tokens = Tokenize(title);
        if (tokens.Length == 0) return 0;

        double affinitySum = 0;
        double maxW = profile.MaxTokenWeight > 0 ? profile.MaxTokenWeight : 1.0;
        foreach (var token in tokens)
        {
            if (profile.TokenVector.TryGetValue(token, out double pw))
                affinitySum += pw / maxW;   // 0..1 por token
        }
        double contentScore = Math.Min(1.0, affinitySum / Math.Sqrt(tokens.Length)) * ContentWeight;

        double artistBonus = 0;
        var normArtist = NormalizeArtist(artist);
        if (normArtist.Length > 0 && profile.TopArtists.Count > 0)
        {
            double topArtistW = profile.TopArtists[0].Value;
            var match = profile.TopArtists.FirstOrDefault(a => ArtistMatches(a.Key, normArtist));
            if (match.Key != null && topArtistW > 0)
                artistBonus = ArtistBonusMax * (match.Value / topArtistW);
        }

        // El bonus de genero se escala por el peso RELATIVO de ese genero en el perfil,
        // igual que el de artista. Antes era binario: un genero residual (medido: una
        // "ranchera" con peso 0.04 frente a 26.56 de "balada") recibia el bonus completo.
        double genreBonus = 0;
        var genre = DetectGenre(title, artist);
        if (genre != null && profile.TopGenres.Count > 0)
        {
            double topGenreW = profile.TopGenres[0].Value;
            var gMatch = profile.TopGenres.FirstOrDefault(g => g.Key == genre);
            if (gMatch.Key != null && topGenreW > 0)
                genreBonus = GenreBonusValue * (gMatch.Value / topGenreW);
        }

        // Refuerzo explícito si el artista es uno de los favoritos del usuario.
        double favoriteBonus = 0;
        if (normArtist.Length > 0 && profile.FavoriteArtists.Count > 0 &&
            profile.FavoriteArtists.Any(fa => ArtistMatches(fa, normArtist)))
            favoriteBonus = FavoriteBonusValue;

        // Señal negativa: si el usuario saltea a este artista la mayoría de las veces,
        // dejar de recomendarlo. Antes un artista muy salteado seguía sumando por
        // ArtistBonus y podía colarse igual.
        double skipPenalty = 0;
        if (normArtist.Length > 0 && profile.SkippedArtists.TryGetValue(normArtist, out double skipRatio))
            skipPenalty = SkipPenaltyMax * skipRatio;

        return Math.Max(0, contentScore + artistBonus + genreBonus + favoriteBonus - skipPenalty);
    }

    // ──────────────── Ranking final: familiar + descubrimiento ────────────────
    //
    //  ScoreCandidate mide "cuánto se parece a lo que ya escucha": un artista que el
    //  usuario no conoce saca casi 0 por definición. Medido en un perfil real, de 30
    //  recomendaciones solo 9 tenían un score con sentido (artistas ya escuchados); las
    //  otras 21 eran resultados de búsquedas genéricas en el orden en que llegaron,
    //  9 de ellas con score 0.000.
    //
    //  Para lo desconocido hace falta otra señal: cuántas de las canciones del usuario
    //  (semillas) "apuntan" a ese candidato en su lista de parecidas, y cómo de arriba
    //  sale en ellas (Candidate.Discovery).
    //
    //    Score = ScoreCandidate + DiscoveryWeight × Discovery normalizado (0..1)
    //
    //  Y como las dos señales no son comparables entre sí, no se deja que una tape a la
    //  otra al ordenar: la lista se compone con una cuota, NewArtistShare de artistas
    //  nuevos y el resto de artistas conocidos, cada grupo ordenado por su score.

    private const double DiscoveryWeight = 0.45;
    private const double NewArtistShare = 0.6;
    private const int MaxPerArtist = 2;

    /// <summary>
    /// Por debajo de este score no hay ninguna señal de que al usuario le vaya a gustar.
    /// Esos candidatos solo se usan para no devolver una lista casi vacía.
    /// </summary>
    private const double MinSignalScore = 0.05;
    private const int MinListSize = 10;

    // Pequeña aleatoriedad SOLO para decidir el orden entre candidatos casi empatados
    // (el score que se devuelve es el real): sin ella el ranking era idéntico sesión
    // tras sesión.
    private const double ExplorationJitter = 0.04;

    /// <param name="title">
    /// Título del candidato. Muchas subidas no oficiales llevan como "artista" el canal
    /// que las subió y el artista real solo en el título ("Don Omar - Pobre Diabla",
    /// subido por un canal cualquiera): también se mira ahí.
    /// </param>
    public static bool IsKnownArtist(UserProfile profile, string normArtist, string title = "")
    {
        if (normArtist.Length > 0 && profile.KnownArtists.Contains(normArtist)) return true;
        // "Don Omar" frente a "Don Omar - Topic" o "Aventura, Don Omar": coincidencia
        // por palabra completa, solo contra los artistas con peso (pocos).
        var titleText = title.ToLowerInvariant();
        bool Mentions(string known) =>
            (normArtist.Length > 0 && ArtistMatches(known, normArtist)) || TitleMentionsArtist(titleText, known);
        return profile.TopArtists.Any(a => Mentions(a.Key)) || profile.FavoriteArtists.Any(Mentions);
    }

    /// <summary>
    /// true si el título (en minúsculas) nombra a ese artista como palabra(s) completa(s).
    /// Se exige un nombre de al menos 4 letras para no dar por artista una palabra corta.
    /// </summary>
    public static bool TitleMentionsArtist(string lowerTitle, string normArtist) =>
        normArtist.Length >= 4 && lowerTitle.Length > normArtist.Length && ArtistMatches(normArtist, lowerTitle);

    /// <summary>
    /// Artista "de verdad" de un candidato, para el tope por artista: si el canal que lo
    /// subió o el título nombran a un artista de peso del usuario, cuenta como ese
    /// artista. Medido con un perfil real, sin esto Don Omar salía 4 veces en una lista
    /// con tope de 2 ("Don Omar", "Don Omar Nation" y una subida de otro canal).
    /// </summary>
    private static string EffectiveArtistKey(UserProfile profile, Candidate c)
    {
        var na = NormalizeArtist(c.Artist);
        var lowerTitle = c.Title.ToLowerInvariant();
        foreach (var known in profile.TopArtists.Select(a => a.Key).Concat(profile.FavoriteArtists))
        {
            if ((na.Length > 0 && ArtistMatches(known, na)) || TitleMentionsArtist(lowerTitle, known))
                return known;
        }
        return na;
    }

    public List<RankedCandidate> Rank(UserProfile profile, IEnumerable<Candidate> candidates, int limit, Random rng)
    {
        var all = candidates.ToList();
        double maxDiscovery = all.Count > 0 ? all.Max(c => c.Discovery) : 0;

        var ranked = all
            .Select(c =>
            {
                double discovery = maxDiscovery > 0 ? c.Discovery / maxDiscovery : 0;
                double score = ScoreCandidate(profile, c.Title, c.Artist) + DiscoveryWeight * discovery;
                bool isNew = !IsKnownArtist(profile, NormalizeArtist(c.Artist), c.Title);
                // Que una semilla la sugiera ya es señal, aunque salga al final de su
                // lista y el score quede bajo: no es relleno.
                bool hasSignal = c.Discovery > 0 || score >= MinSignalScore;
                return (item: new RankedCandidate(c, Math.Round(score, 4), isNew), hasSignal,
                        order: score + (rng.NextDouble() * 2 - 1) * ExplorationJitter);
            })
            .OrderByDescending(x => x.order)
            .ToList();

        // Entre los artistas nuevos, primero los que sugiere alguna semilla. Los que solo
        // salieron en una búsqueda por género ("cumbia éxitos") tienen como única señal
        // llevar la palabra del género en el título; medido con un perfil real, sin este
        // orden ocupaban 8 de los 18 puestos de descubrimiento.
        var fresh = new Queue<RankedCandidate>(ranked
            .Where(r => r.hasSignal && r.item.IsNewArtist)
            .OrderByDescending(r => r.item.Candidate.Discovery > 0) // estable: conserva el orden por score
            .Select(r => r.item));
        var familiar = new Queue<RankedCandidate>(ranked.Where(r => r.hasSignal && !r.item.IsNewArtist).Select(r => r.item));
        var filler = new Queue<RankedCandidate>(ranked.Where(r => !r.hasSignal).Select(r => r.item));

        var result = new List<RankedCandidate>();
        var perArtist = new Dictionary<string, int>();
        var songKeys = new HashSet<string>();

        // Diversidad: máximo MaxPerArtist por artista, y nunca dos subidas de la misma
        // canción (otro canal, otra versión) en la misma lista.
        bool TryTake(Queue<RankedCandidate> queue)
        {
            while (queue.Count > 0)
            {
                var next = queue.Dequeue();
                var artistKey = EffectiveArtistKey(profile, next.Candidate);
                if (perArtist.GetValueOrDefault(artistKey) >= MaxPerArtist) continue;
                var songKey = SongKey(next.Candidate.Title, next.Candidate.Artist);
                if (songKey != null && !songKeys.Add(songKey)) continue;
                perArtist[artistKey] = perArtist.GetValueOrDefault(artistKey) + 1;
                result.Add(next);
                return true;
            }
            return false;
        }

        int newTaken = 0;
        while (result.Count < limit && (fresh.Count > 0 || familiar.Count > 0))
        {
            // ¿Le toca a un artista nuevo para mantener la cuota? Si su cola se acabó,
            // se sigue con la otra en vez de dejar huecos.
            bool wantNew = newTaken < Math.Ceiling(NewArtistShare * (result.Count + 1));
            if (wantNew)
            {
                if (TryTake(fresh)) newTaken++;
                else if (!TryTake(familiar)) break;
            }
            else if (!TryTake(familiar))
            {
                if (TryTake(fresh)) newTaken++;
                else break;
            }
        }

        // Relleno sin señal: solo si la lista se queda muy corta.
        while (result.Count < Math.Min(limit, MinListSize) && TryTake(filler)) { }

        return result;
    }

    // ──────────────── Helpers de matching / calidad ────────────────

    /// <summary>
    /// Coincidencia de artista: igualdad normalizada o aparición como palabra completa.
    /// Antes era Contains en los dos sentidos, que daba falsos positivos ("mora" dentro
    /// de "morat" se llevaba el bonus de artista). Exige longitud ≥3 para lo parcial.
    /// </summary>
    public static bool ArtistMatches(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        if (a.Length < 3 || b.Length < 3) return false;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);

        // Búsqueda manual de "shorter" como palabra(s) completa(s) dentro de "longer".
        // Hace lo mismo que la expresión regular \bshorter\b que había antes, pero sin
        // compilar un patrón nuevo en cada llamada: con el descubrimiento se compara
        // cada candidato contra decenas de artistas y eso sumaba cientos de ms en la Pi.
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
        for (int at = longer.IndexOf(shorter, StringComparison.Ordinal);
             at >= 0;
             at = longer.IndexOf(shorter, at + 1, StringComparison.Ordinal))
        {
            int end = at + shorter.Length;
            bool startOk = at == 0 || !IsWordChar(longer[at - 1]);
            bool endOk = end == longer.Length || !IsWordChar(longer[end]);
            if (startOk && endOk) return true;
        }
        return false;
    }

    /// <summary>
    /// Clave de "misma canción" independiente del vídeo: tokens del título sin
    /// paréntesis, palabras vacías ni el nombre del artista. Sirve para no recomendar
    /// otra subida (o una versión de otro canal) de algo escuchado hace poco. Devuelve
    /// null si el título es demasiado corto para comparar sin falsos positivos.
    /// </summary>
    public static string? SongKey(string title, string artist)
    {
        var artistTokens = new HashSet<string>(Tokenize(artist));
        var tokens = Tokenize(title).Where(t => !artistTokens.Contains(t)).Distinct().OrderBy(t => t).ToList();
        return tokens.Count >= 2 ? string.Join(' ', tokens) : null;
    }

    /// <summary>
    /// Claves de canción del historial reciente (misma ventana que PlayedVideoIds). Como
    /// con los videoIds, pasada la ventana la canción vuelve a ser candidata.
    /// </summary>
    public static HashSet<string> RecentSongKeys(IEnumerable<History> history)
    {
        var now = DateTime.UtcNow;
        return history
            .Where(h => (now - h.PlayedAt).TotalDays <= RecentExclusionWindowDays)
            .Select(h => SongKey(h.Title, h.Artist))
            .Where(k => k != null)
            .Select(k => k!)
            .ToHashSet();
    }

    // Siempre basura, sea cual sea la duración: karaoke, covers, versiones alteradas,
    // tutoriales, popurrís y compilaciones. Las duraciones ("1 hora", "24 horas") NO
    // van aquí: hay canciones reales con esos títulos y el tope de 10 min ya cubre los
    // vídeos largos.
    private static readonly Regex AlwaysJunkTitle = new(
        @"\b(megamix|enganchad[oa]s?|mosaico|po(t-?)?po?u?rr?[ií]|compilaci[oó]n|compilation|full album|[aá]lbum completo|" +
        @"karaoke|cover|tutorial|lesson|nightcore|sped\s*up|slowed|8d\s*audio|" +
        @"greatest hits|top\s*\d+\s*(mix|canciones))\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // "mix" solo es basura cuando es un mix de verdad (largo, o con año/volumen/
    // "bailable"/"éxitos"...). Así no caen "Little Mix" ni versiones tipo
    // "Ven Bailalo (Reggaeton Mix)" de duración normal.
    private static readonly Regex MixWord = new(@"\bmix\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex MixContext = new(
        @"\b((19|20)\d\d|vol\.?\s*\d+|bailable|[eé]xitos|lo mejor|cumbias|baladas|salsas|bachatas)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Popurrí: varias canciones en un título ("A / B / C").
    private static readonly Regex Medley = new(@"\s/\s.+\s/\s", RegexOptions.Compiled);

    /// <summary>
    /// true si el candidato es "basura" para recomendación: duración inválida o larga
    /// (mix o compilación), título con patrones no deseados, popurrí o mix. Medido en la
    /// API real: colaba mixes de 20-45 min ("Mix Cumbias 2000 Lo Mejor", 2816 s).
    /// Duración 0 = desconocida (algunas fuentes no la traen): se juzga solo por título,
    /// para no vaciar el descubrimiento si una fuente entera viene sin duración.
    /// </summary>
    public static bool IsJunkCandidate(string title, int durationSeconds)
    {
        if (durationSeconds > 600) return true; // >10 min = mix/compilación
        if (string.IsNullOrWhiteSpace(title)) return true;
        if (AlwaysJunkTitle.IsMatch(title) || Medley.IsMatch(title)) return true;
        return MixWord.IsMatch(title) && (durationSeconds > 300 || MixContext.IsMatch(title));
    }

    // ──────────────── Botones de género: mixes a la medida ────────────────
    //
    //  Orden por niveles, y dentro de cada nivel por afinidad con el usuario:
    //    1º mixes que no ha escuchado en las últimas 3 semanas
    //    2º mixes escuchados hace poco (así el botón rota y no repite siempre el mismo)
    //    3º canciones sueltas del género
    //
    //  Los niveles son fijos y no un bonus sumado al score a propósito: un mix ya
    //  escuchado tiene su propio título dentro del perfil del usuario, así que se
    //  puntuaba alto a sí mismo y un "castigo" numérico no bastaba para hacerlo rotar.
    //
    //  Afinidad = ScoreCandidate            (parecido general con lo que escucha)
    //           + ArtistMentionBonus × n    (artistas suyos nombrados en el título; tope 3)
    //           + FavoriteVideoBonus        si ese vídeo concreto es favorito suyo
    //           + RelevanceWeight × pos     (los primeros resultados de la búsqueda, algo mejor)

    private const double ArtistMentionBonus = 0.15;
    private const int MaxArtistMentions = 3;
    private const double FavoriteVideoBonus = 0.3;
    private const double RelevanceWeight = 0.15;
    private const double GenreJitter = 0.05;

    // Un vídeo de más de 10 min en estas búsquedas es un mix (o un álbum o concierto
    // entero, que para el caso sirve igual). El título solo decide cuando la fuente no
    // trae duración: medido con un perfil real, "Mix Cumbia 3" de 4 minutos (un
    // enganchado corto) se colaba por delante de mixes de una hora, y "Little Mix" es
    // un grupo, no un mix.
    private const int MixMinSeconds = 600;
    // Más de 3 horas: directos grabados y bucles de "10 horas", no un mix para escuchar.
    private const int MixMaxSeconds = 3 * 3600;

    private static readonly Regex MixTitle = new(
        @"\b(mix|megamix|minimix|enganchad[oa]s?|sesi[oó]n|session|dj\s*set)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Artistas con peso en el gusto del usuario: los más escuchados y los de sus favoritos.</summary>
    public static List<string> TasteArtists(UserProfile profile) =>
        profile.TopArtists.Select(a => a.Key).Concat(profile.FavoriteArtists).Distinct().ToList();

    /// <summary>
    /// Minúsculas, sin tildes y sin signos: "Guns N' Roses", "GUNS N ROSES" y "Los Ángeles
    /// Azules" / "Los Angeles Azules" quedan iguales. Los títulos de los mixes escriben
    /// los nombres de cualquier manera.
    /// </summary>
    public static string Fold(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var decomposed = text.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder(decomposed.Length);
        bool lastWasSpace = true;
        foreach (var c in decomposed)
        {
            var category = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (category == System.Globalization.UnicodeCategory.NonSpacingMark) continue; // la tilde
            if (c == '\'' || c == '’') continue; // apóstrofo: se pega ("n' roses" -> "n roses")
            if (char.IsLetterOrDigit(c)) { sb.Append(c); lastWasSpace = false; }
            else if (!lastWasSpace) { sb.Append(' '); lastWasSpace = true; }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// true si el vídeo es de ese artista o lo nombra en el título (comparando sin tildes
    /// ni signos, por palabra completa). Nombres de menos de 4 letras solo cuentan si
    /// son el canal, para no confundir una palabra corta del título con un artista.
    /// </summary>
    public static bool MentionsArtist(string title, string uploader, string normArtist)
    {
        var artist = Fold(normArtist);
        if (artist.Length == 0) return false;
        var channel = Fold(NormalizeArtist(uploader));
        if (channel.Length > 0 && ArtistMatches(artist, channel)) return true;
        if (artist.Length < 4) return false;
        var foldedTitle = Fold(title);
        return foldedTitle.Length > artist.Length && ArtistMatches(artist, foldedTitle);
    }

    public static bool IsMix(string title, int durationSeconds) =>
        durationSeconds >= MixMinSeconds || (durationSeconds == 0 && MixTitle.IsMatch(title));

    /// <summary>
    /// Ordena los candidatos de un botón de género para un usuario. El primero es lo que
    /// la app reproduce al pulsar el botón.
    /// </summary>
    /// <param name="favoriteVideoIds">Vídeos marcados como favoritos por el usuario.</param>
    public List<RankedGenreCandidate> RankGenre(UserProfile profile, IEnumerable<GenreCandidate> candidates,
        ISet<string> favoriteVideoIds, Random rng)
    {
        var tasteArtists = TasteArtists(profile);

        var scored = new List<(RankedGenreCandidate item, int tier, double order)>();
        foreach (var c in candidates)
        {
            if (string.IsNullOrWhiteSpace(c.Title)) continue;
            if (c.Duration < 0 || c.Duration > MixMaxSeconds) continue;      // directo o bucle de horas
            if (profile.SkippedVideoIds.Contains(c.VideoId)) continue;       // lo saltó: no insistir

            bool isMix = IsMix(c.Title, c.Duration);
            int mentions = Math.Min(MaxArtistMentions, tasteArtists.Count(a => MentionsArtist(c.Title, c.Artist, a)));

            double score = ScoreCandidate(profile, c.Title, c.Artist)
                         + ArtistMentionBonus * mentions
                         + (favoriteVideoIds.Contains(c.VideoId) ? FavoriteVideoBonus : 0)
                         + RelevanceWeight * (1 - Math.Min(c.BestPosition, 20) / 20.0);

            bool recentlyPlayed = profile.PlayedVideoIds.Contains(c.VideoId);
            int tier = !isMix ? 2 : recentlyPlayed ? 1 : 0;

            scored.Add((new RankedGenreCandidate(c, Math.Round(score, 4), isMix), tier,
                        score + (rng.NextDouble() * 2 - 1) * GenreJitter));
        }

        // Variedad: máximo 2 por artista o canal, y nunca el mismo mix dos veces (es
        // habitual que dos canales suban el mismo vídeo con el mismo título). Si el
        // título o el canal nombran a un artista del usuario, cuenta como ese artista:
        // medido con un perfil real, 4 de los 5 primeros de "Bachata" eran de Dalvin La
        // Melodia bajo cuatro nombres de canal distintos.
        var perSource = new Dictionary<string, int>();
        var seenTitles = new HashSet<string>();
        var result = new List<RankedGenreCandidate>();
        foreach (var (item, _, _) in scored.OrderBy(x => x.tier).ThenByDescending(x => x.order))
        {
            var c = item.Candidate;
            var source = tasteArtists.FirstOrDefault(a => MentionsArtist(c.Title, c.Artist, a))
                         ?? NormalizeArtist(c.Artist);
            if (perSource.GetValueOrDefault(source) >= MaxPerArtist) continue;
            if (!seenTitles.Add(Fold(c.Title))) continue;
            perSource[source] = perSource.GetValueOrDefault(source) + 1;
            result.Add(item);
        }
        return result;
    }

    // ──────────────── Generación de queries ────────────────

    public string[] GenerateSearchQueries(UserProfile profile)
    {
        var queries = new List<string>();
        var rng = new Random();

        foreach (var artist in profile.TopArtists.Take(3))
            queries.Add($"{artist.Key} éxitos");

        foreach (var genre in profile.TopGenres.Take(2))
        {
            if (GenreSearchQueries.TryGetValue(genre.Key, out var gq))
                queries.Add(gq.OrderBy(_ => rng.Next()).First().Replace("{year}", DateTime.UtcNow.Year.ToString()));
        }

        // Ya no se busca "música similar a {artista}": como texto de búsqueda devolvía
        // sobre todo listas y mixes de horas (descartados luego como basura). Las
        // canciones parecidas salen ahora de las semillas (ver PickSeeds).

        return queries.Distinct().ToArray();
    }
}
