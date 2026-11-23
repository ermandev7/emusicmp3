using System;
using System.Collections.Generic;
using System.Linq;

namespace eMusicApi.Services;

/// <summary>Una búsqueda de mixes de un género y el filtro de Piped con que se hace.</summary>
public sealed record GenreQuery(string Text, string Filter);

/// <summary>
/// Un botón de género de la pantalla de inicio de la app ("Cumbia", "Salsa"...).
/// </summary>
/// <param name="Key">Identificador interno del género.</param>
/// <param name="AppQuery">
/// El texto EXACTO que la app envía a /api/search al pulsar el botón (está fijo en
/// HomeScreen.kt de Android y Theme.swift de iOS). Es la única forma de saber, sin
/// cambiar la app, que la petición viene de un botón de género y no del buscador.
/// </param>
/// <param name="MixQueries">Búsquedas con las que se reúnen mixes de ese género.</param>
public sealed record GenreStation(string Key, string AppQuery, GenreQuery[] MixQueries);

public static class GenreStations
{
    // "videos" y "music_videos" devuelven mixes largos de DJ; el filtro por defecto del
    // buscador ("music_songs") devuelve casi solo canciones sueltas. Medido el 23/11/2026
    // con "cumbia mix": 20 de 20 resultados de más de 10 min con estos filtros, frente a
    // 3 de 20 con la búsqueda que hacía el botón.
    private const string Videos = "videos";
    private const string MusicVideos = "music_videos";

    private static GenreStation Station(string key, string appQuery, params string[] mixQueries) =>
        new(key, appQuery, mixQueries
            .Select((text, i) => new GenreQuery(text, i == 0 ? Videos : MusicVideos))
            .ToArray());

    public static readonly GenreStation[] All =
    {
        Station("salsa", "salsa éxitos Héctor Lavoe Marc Anthony Rubén Blades mix",
            "salsa mix", "salsa mix éxitos", "salsa romántica mix"),
        Station("cumbia", "cumbia éxitos Los Ángeles Azules Grupo 5 mix bailable",
            "cumbia mix", "cumbia mix {year}", "cumbias bailables mix"),
        Station("rock", "rock clásico Aerosmith Guns N Roses Metallica Mago de Oz éxitos",
            "rock clásico mix", "rock en español mix", "rock mix éxitos"),
        Station("reggaeton", "reggaeton éxitos 2024 Bad Bunny Daddy Yankee Karol G mix",
            "reggaeton mix", "reggaeton mix {year}", "reggaeton antiguo mix"),
        Station("electronic", "electronic dance music éxitos David Guetta Martin Garrix Avicii mix",
            "música electrónica mix", "EDM mix {year}", "electronic dance mix"),
        Station("reggae", "reggae éxitos Bob Marley UB40 mix popular",
            "reggae mix", "reggae mix éxitos", "reggae en español mix"),
        Station("bachata", "bachata éxitos Romeo Santos Aventura Prince Royce mix",
            "bachata mix", "bachata mix {year}", "bachata romántica mix"),
        Station("pop", "pop latino éxitos 2024 Shakira Luis Fonsi Sebastián Yatra mix",
            "pop latino mix", "pop mix {year}", "pop en español mix"),
    };

    private static readonly Dictionary<string, GenreStation> ByAppQuery =
        All.ToDictionary(s => Normalize(s.AppQuery), s => s);

    private static string Normalize(string query) =>
        string.Join(' ', query.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// true si el texto buscado es exactamente el de un botón de género de la app. Una
    /// búsqueda escrita a mano ("cumbia", "cumbia mix"...) nunca coincide: el buscador
    /// normal no se toca.
    /// </summary>
    public static bool TryMatch(string? query, out GenreStation station)
    {
        station = null!;
        return !string.IsNullOrWhiteSpace(query) && ByAppQuery.TryGetValue(Normalize(query), out station!);
    }
}
