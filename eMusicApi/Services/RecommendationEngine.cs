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
}

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

    private static readonly Dictionary<string, string[]> GenreSearchQueries = new()
    {
        ["salsa"]      = new[] { "salsa éxitos", "salsa romántica mix", "salsa clásica", "lo mejor de la salsa" },
        ["bachata"]    = new[] { "bachata éxitos", "bachata romántica", "bachata sensual mix" },
        ["reggaeton"]  = new[] { "reggaeton éxitos 2024", "reggaeton mix", "perreo mix" },
        ["cumbia"]     = new[] { "cumbia éxitos", "cumbia mix bailable", "cumbia clásica" },
        ["merengue"]   = new[] { "merengue éxitos", "merengue mix bailable" },
        ["vallenato"]  = new[] { "vallenato éxitos", "vallenato romántico mix" },
        ["rock"]       = new[] { "rock en español éxitos", "rock clásico mix", "rock latino" },
        ["pop"]        = new[] { "pop éxitos 2024", "pop latino mix", "pop en español" },
        ["rap"]        = new[] { "rap éxitos", "hip hop mix", "rap en español mix" },
        ["trap"]       = new[] { "trap latino mix", "trap éxitos 2024" },
        ["balada"]     = new[] { "baladas románticas mix", "baladas en español" },
        ["ranchera"]   = new[] { "rancheras éxitos", "música mexicana mix", "mariachi éxitos" },
        ["corrido"]    = new[] { "corridos tumbados mix", "corridos éxitos 2024" },
        ["electronic"] = new[] { "electronic dance mix", "EDM mix 2024" },
        ["jazz"]       = new[] { "jazz clásico", "smooth jazz mix" },
        ["blues"]      = new[] { "blues éxitos", "blues clásico mix" },
        ["reggae"]     = new[] { "reggae éxitos", "reggae mix" },
        ["clasica"]    = new[] { "música clásica famosa", "piano clásico" },
        ["kpop"]       = new[] { "kpop éxitos 2024", "kpop mix" },
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
        if (combined.Contains("amor") || combined.Contains("corazón")) return "balada";
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

    public UserProfile BuildProfile(List<History> history, List<Favorite>? favorites = null)
    {
        favorites ??= new List<Favorite>();
        if (history.Count == 0 && favorites.Count == 0)
            return new UserProfile();

        var tokenFreq = new Dictionary<string, double>();
        var tokenDocFreq = new Dictionary<string, int>();
        var artistWeights = new Dictionary<string, double>();
        var genreWeights = new Dictionary<string, double>();
        var playedIds = new HashSet<string>();
        var favoriteArtists = new HashSet<string>();
        // Reproducciones y skips por artista, para la señal negativa.
        var artistPlays = new Dictionary<string, int>();
        var artistSkips = new Dictionary<string, int>();
        var now = DateTime.UtcNow;

        // Acumula una "canción" (del historial o favorita) en los vectores del perfil.
        void Accumulate(string videoId, string title, string artist, double weight)
        {
            if (!string.IsNullOrEmpty(videoId)) playedIds.Add(videoId);

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
            double recency = Math.Exp(-0.05 * daysSince);
            // Un tema salteado apenas aporta al perfil (antes 0.3, que todavía era
            // un voto a favor bastante fuerte de algo que al usuario no le gustó).
            double skipPenalty = entry.SkippedEarly ? 0.1 : 1.0;
            double downloadBonus = entry.IsDownloaded ? 1.5 : 1.0;
            double weight = entry.PlayCount * recency * skipPenalty * downloadBonus;
            Accumulate(entry.VideoId, entry.Title, entry.Artist, weight);

            var artistKey = NormalizeArtist(entry.Artist);
            if (artistKey.Length > 0)
            {
                artistPlays[artistKey] = artistPlays.GetValueOrDefault(artistKey) + 1;
                if (entry.SkippedEarly)
                    artistSkips[artistKey] = artistSkips.GetValueOrDefault(artistKey) + 1;
            }
        }

        // Favoritos: peso fuerte y constante. También registramos sus artistas.
        foreach (var fav in favorites)
        {
            Accumulate(fav.Id, fav.Title, fav.Artist, FavoriteWeight);
            var na = NormalizeArtist(fav.Artist);
            if (na.Length > 0) favoriteArtists.Add(na);
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
            double ratio = artistSkips.GetValueOrDefault(artist) / (double)plays;
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
            SkippedArtists = skippedArtists
        };
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
            var match = profile.TopArtists.FirstOrDefault(a =>
                a.Key == normArtist || a.Key.Contains(normArtist) || normArtist.Contains(a.Key));
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
            profile.FavoriteArtists.Any(fa => fa == normArtist || fa.Contains(normArtist) || normArtist.Contains(fa)))
            favoriteBonus = FavoriteBonusValue;

        // Señal negativa: si el usuario saltea a este artista la mayoría de las veces,
        // dejar de recomendarlo. Antes un artista muy salteado seguía sumando por
        // ArtistBonus y podía colarse igual.
        double skipPenalty = 0;
        if (normArtist.Length > 0 && profile.SkippedArtists.TryGetValue(normArtist, out double skipRatio))
            skipPenalty = SkipPenaltyMax * skipRatio;

        return Math.Max(0, contentScore + artistBonus + genreBonus + favoriteBonus - skipPenalty);
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
                queries.Add(gq.OrderBy(_ => rng.Next()).First());
        }

        if (profile.TopArtists.Count > 0)
            queries.Add($"música similar a {profile.TopArtists[0].Key}");

        return queries.Distinct().ToArray();
    }
}
