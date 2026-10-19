using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using eMusicApi.Models;

namespace eMusicApi.Services;

public class UserProfile
{
    public Dictionary<string, double> TokenVector { get; init; } = new();
    /// <summary>IDF por token (para ponderar los tokens del candidato, no solo los del perfil).</summary>
    public Dictionary<string, double> IdfVector { get; init; } = new();
    public List<KeyValuePair<string, double>> TopArtists { get; init; } = new();
    public List<KeyValuePair<string, double>> TopGenres { get; init; } = new();
    public HashSet<string> PlayedVideoIds { get; init; } = new();
    /// <summary>Artistas marcados como favoritos (señal explícita).</summary>
    public HashSet<string> FavoriteArtists { get; init; } = new();
    /// <summary>Tasa de skip por artista [0,1]: cuánto suele saltar el usuario a ese artista (señal negativa).</summary>
    public Dictionary<string, double> ArtistSkipRate { get; init; } = new();
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
        "tema", "cancion", "musica", "exitos", "mix", "vol"
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
    //  W(i) = PlaysEscuchadas × e^(-0.05 × days) × DownloadBonus
    //         (PlaysEscuchadas = PlayCount, menos 1 si la última vez se saltó)
    //  TF(t) = Σ W(i)  para tracks que contienen token t
    //  IDF(t) = ln((N+1)/(df+1)) + 1
    //  ProfileVector[t] = TF(t) × IDF(t)

    // Peso de un favorito (corazón). Señal explícita fuerte, sin decay temporal:
    // equivale a una canción bastante escuchada.
    private const double FavoriteWeight = 8.0;

    /// <param name="excludedArtists">
    /// Artistas marcados "no recomendar" (normalizados). Se quitan del perfil: si no, un
    /// artista muy escuchado pero excluido seguía dominando los top artistas, las
    /// búsquedas ("música similar a X") y el género, y se recomendaba "más de lo mismo"
    /// que el usuario había rechazado.
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
        // Conteo de reproducciones y skips por artista para la señal negativa.
        var artistPlays = new Dictionary<string, int>();
        var artistSkips = new Dictionary<string, int>();
        var now = DateTime.UtcNow;

        // Acumula una "canción" (del historial o favorita) en los vectores del perfil.
        void Accumulate(string videoId, string title, string artist, double weight)
        {
            if (!string.IsNullOrEmpty(videoId)) playedIds.Add(videoId);

            var tokens = Tokenize(title);
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
            // SkippedEarly = la última vez se saltó en los primeros segundos (el servidor
            // lo resetea al volver a escucharla). Esa reproducción saltada no cuenta como
            // gusto: una canción saltada a la primera aporta 0 al perfil.
            int listenedPlays = entry.SkippedEarly ? entry.PlayCount - 1 : entry.PlayCount;
            if (listenedPlays > 0)
            {
                double daysSince = Math.Max(0, (now - entry.PlayedAt).TotalDays);
                double recency = Math.Exp(-0.05 * daysSince);
                double downloadBonus = entry.IsDownloaded ? 1.5 : 1.0;
                double weight = listenedPlays * recency * downloadBonus;
                Accumulate(entry.VideoId, entry.Title, entry.Artist, weight);
            }

            // Canciones y skips por artista (señal negativa posterior). Se cuenta por
            // canción, no por reproducción: saltar una vez un tema escuchado 70 veces no
            // son 70 skips.
            var na = NormalizeArtist(entry.Artist);
            if (na.Length > 0)
            {
                artistPlays.TryGetValue(na, out int pc);
                artistPlays[na] = pc + 1;
                if (entry.SkippedEarly)
                {
                    artistSkips.TryGetValue(na, out int sc);
                    artistSkips[na] = sc + 1;
                }
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
        var idfVector = new Dictionary<string, double>();
        foreach (var (token, tf) in tokenFreq)
        {
            int df = tokenDocFreq.GetValueOrDefault(token, 1);
            double idf = Math.Log((double)(n + 1) / (df + 1)) + 1.0;
            profileVector[token] = tf * idf;
            idfVector[token] = idf;
        }

        // Tasa de skip por artista: canciones saltadas / canciones en el historial. Solo
        // con muestra suficiente (≥3 canciones) para no penalizar por un skip accidental.
        var artistSkipRate = new Dictionary<string, double>();
        foreach (var (artist, plays) in artistPlays)
        {
            if (plays < 3) continue;
            int skips = artistSkips.GetValueOrDefault(artist, 0);
            double rate = (double)skips / plays;
            if (rate > 0) artistSkipRate[artist] = rate;
        }

        return new UserProfile
        {
            TokenVector = profileVector,
            IdfVector = idfVector,
            TopArtists = artistWeights.OrderByDescending(kv => kv.Value).Take(5).ToList(),
            TopGenres = genreWeights.OrderByDescending(kv => kv.Value).Take(3).ToList(),
            PlayedVideoIds = playedIds,
            FavoriteArtists = favoriteArtists,
            ArtistSkipRate = artistSkipRate
        };
    }

    // ──────────────── Helpers de matching / calidad ────────────────

    /// <summary>
    /// Clave de "misma canción" independiente del vídeo: tokens del título sin
    /// paréntesis, palabras vacías ni el nombre del artista. Sirve para no recomendar
    /// otra subida (o una versión de otro canal) de algo que el usuario ya escuchó.
    /// Devuelve null si el título es demasiado corto para comparar sin falsos positivos.
    /// </summary>
    public static string? SongKey(string title, string artist)
    {
        var artistTokens = new HashSet<string>(Tokenize(artist));
        var tokens = Tokenize(title).Where(t => !artistTokens.Contains(t)).Distinct().OrderBy(t => t).ToList();
        return tokens.Count >= 2 ? string.Join(' ', tokens) : null;
    }

    /// <summary>
    /// Coincidencia de artista robusta: igualdad normalizada o aparición como palabra
    /// completa (evita falsos positivos tipo "Mora"⊂"Morat"). Exige longitud ≥3.
    /// </summary>
    public static bool ArtistMatches(string a, string b)
    {
        if (a.Length == 0 || b.Length == 0) return false;
        if (a == b) return true;
        if (a.Length < 3 || b.Length < 3) return false;
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);
        return Regex.IsMatch(longer, $@"\b{Regex.Escape(shorter)}\b");
    }

    // Siempre basura, sea cual sea la duración: karaoke, covers, versiones alteradas,
    // tutoriales, popurrís y compilaciones. Las duraciones ("1 hora", "24 horas") NO
    // van aquí: hay canciones reales con esos títulos y el tope de 10 min ya cubre los
    // vídeos largos.
    private static readonly Regex AlwaysJunkTitle = new(
        @"\b(megamix|enganchad[oa]s?|compilaci[oó]n|compilation|full album|[aá]lbum completo|" +
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
    /// (mix o compilación), título con patrones no deseados, popurrí o mix. Con
    /// durationSeconds=0 se descarta porque no se puede validar.
    /// </summary>
    public static bool IsJunkCandidate(string title, int durationSeconds)
    {
        if (durationSeconds <= 0 || durationSeconds > 600) return true; // >10 min = mix/compilación
        if (string.IsNullOrWhiteSpace(title)) return true;
        if (AlwaysJunkTitle.IsMatch(title) || Medley.IsMatch(title)) return true;
        return MixWord.IsMatch(title) && (durationSeconds > 300 || MixContext.IsMatch(title));
    }

    // ──────────────── Scoring de candidatos ────────────────
    //
    //  Score = CosineSim(Profile, Candidate) + ArtistBonus + GenreBonus
    //
    //  CosineSim = (P · C) / (|P| × |C|)
    //  ArtistBonus ∈ [0, 0.3]  proporcional al peso del artista en el perfil
    //  GenreBonus  = 0.2 si el género coincide con los top del usuario

    public double ScoreCandidate(UserProfile profile, string title, string artist)
    {
        var tokens = Tokenize(title);
        if (tokens.Length == 0) return 0;

        // El token del candidato pesa por su IDF en el perfil: así los tokens raros y
        // significativos (nombre propio, palabra distintiva) mandan, y los comunes
        // ("amor", "remix"...) casi no suman. Antes todos pesaban 1.0 y una canción
        // random que compartía una palabra común puntuaba alto.
        double dotProduct = 0;
        double candidateMagSq = 0;
        double defaultIdf = 1.0;
        foreach (var token in tokens)
        {
            double cw = profile.IdfVector.GetValueOrDefault(token, defaultIdf);
            if (profile.TokenVector.TryGetValue(token, out double pw))
                dotProduct += pw * cw;
            candidateMagSq += cw * cw;
        }

        double profileMagSq = 0;
        foreach (var v in profile.TokenVector.Values)
            profileMagSq += v * v;

        double cosineSim = 0;
        if (profileMagSq > 0 && candidateMagSq > 0)
            cosineSim = dotProduct / (Math.Sqrt(profileMagSq) * Math.Sqrt(candidateMagSq));

        var normArtist = NormalizeArtist(artist);

        double artistBonus = 0;
        if (normArtist.Length > 0 && profile.TopArtists.Count > 0)
        {
            double maxW = profile.TopArtists[0].Value;
            var match = profile.TopArtists.FirstOrDefault(a => ArtistMatches(a.Key, normArtist));
            if (match.Key != null && maxW > 0)
                artistBonus = 0.3 * (match.Value / maxW);
        }

        double genreBonus = 0;
        var genre = DetectGenre(title, artist);
        if (genre != null && profile.TopGenres.Any(g => g.Key == genre))
            genreBonus = 0.2;

        // Refuerzo explícito si el artista es uno de los favoritos del usuario.
        double favoriteBonus = 0;
        if (normArtist.Length > 0 && profile.FavoriteArtists.Count > 0 &&
            profile.FavoriteArtists.Any(fa => ArtistMatches(fa, normArtist)))
            favoriteBonus = 0.25;

        // Señal negativa: si el usuario suele saltar a este artista, resta score
        // proporcional a la tasa de skip (hasta -0.3).
        double skipPenalty = 0;
        if (normArtist.Length > 0 && profile.ArtistSkipRate.Count > 0)
        {
            var hit = profile.ArtistSkipRate.FirstOrDefault(kv => ArtistMatches(kv.Key, normArtist));
            if (hit.Key != null) skipPenalty = 0.3 * hit.Value;
        }

        return cosineSim + artistBonus + genreBonus + favoriteBonus - skipPenalty;
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
