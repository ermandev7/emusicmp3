using System.Linq;
using eMusicApi.Data;
using eMusicApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();

// Compresión de respuestas (gzip/brotli) — las búsquedas/recomendaciones son JSON
// grande; comprimir ahorra datos móviles y acelera la carga en el cliente.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.BrotliCompressionProvider>();
    options.Providers.Add<Microsoft.AspNetCore.ResponseCompression.GzipCompressionProvider>();
    options.MimeTypes = Microsoft.AspNetCore.ResponseCompression.ResponseCompressionDefaults
        .MimeTypes.Concat(new[] { "application/json" });
});
builder.Services.AddHttpClient();
builder.Services.AddSingleton<MusicExtractionService>();
builder.Services.AddSingleton<RecommendationEngine>();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());
});

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=emusic.db"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();

    // Agregar columnas si no existen (SQLite no las agrega con EnsureCreated si la tabla ya existe).
    // Se comprueba con pragma_table_info ANTES de intentar el ALTER: antes se intentaba
    // siempre y se descartaba el error si ya existia, pero EF Core registraba el fallo
    // igualmente en los logs en cada arranque (6 lineas de "fail:" siempre, sin aportar
    // nada). Comprobando antes, solo se ejecuta el ALTER la primera vez que hace falta.
    (string Table, string Column, string Sql)[] columnMigrations = {
        ("History", "PlayCount", "ALTER TABLE History ADD COLUMN PlayCount INTEGER NOT NULL DEFAULT 1"),
        ("History", "UserId", "ALTER TABLE History ADD COLUMN UserId TEXT NOT NULL DEFAULT ''"),
        ("Favorites", "UserId", "ALTER TABLE Favorites ADD COLUMN UserId TEXT NOT NULL DEFAULT ''"),
        ("Playlists", "UserId", "ALTER TABLE Playlists ADD COLUMN UserId TEXT NOT NULL DEFAULT ''"),
        ("History", "SkippedEarly", "ALTER TABLE History ADD COLUMN SkippedEarly INTEGER NOT NULL DEFAULT 0"),
        ("History", "IsDownloaded", "ALTER TABLE History ADD COLUMN IsDownloaded INTEGER NOT NULL DEFAULT 0"),
    };
    foreach (var (table, column, sql) in columnMigrations)
    {
        var columnExists = db.Database
            .SqlQueryRaw<long>($"SELECT COUNT(*) AS Value FROM pragma_table_info('{table}') WHERE name = '{column}'")
            .AsEnumerable().FirstOrDefault() > 0;
        if (columnExists) continue;

        try { db.Database.ExecuteSqlRaw(sql); }
        catch (Exception ex) { Console.WriteLine($"[Esquema] No se pudo anadir {table}.{column}: {ex.Message}"); }
    }

    // Favorites usaba Id (=videoId) como clave primaria GLOBAL: si dos usuarios daban
    // "me gusta" a la misma canción, el segundo chocaba con un error 500. Se recrea la
    // tabla con clave compuesta (Id, UserId) para que cada usuario tenga sus propios
    // favoritos independientes. Se detecta si ya está migrada (columna UserId ya
    // forma parte de la clave primaria) para no rehacerlo en cada arranque, y todo
    // el paso se hace en una única transacción: si algo falla a mitad, no queda a
    // medias.
    try
    {
        var userIdIsPartOfKey = db.Database
            .SqlQueryRaw<long>("SELECT COUNT(*) AS Value FROM pragma_table_info('Favorites') WHERE name = 'UserId' AND pk > 0")
            .AsEnumerable().FirstOrDefault() > 0;

        if (!userIdIsPartOfKey)
        {
            using var tx = db.Database.BeginTransaction();
            db.Database.ExecuteSqlRaw("ALTER TABLE Favorites RENAME TO Favorites_old_singlekey");
            db.Database.ExecuteSqlRaw(@"CREATE TABLE Favorites (
                Id TEXT NOT NULL,
                Title TEXT NOT NULL,
                Artist TEXT NOT NULL,
                ThumbnailUrl TEXT NOT NULL,
                Duration INTEGER NOT NULL,
                UserId TEXT NOT NULL DEFAULT '',
                CONSTRAINT PK_Favorites PRIMARY KEY (Id, UserId)
            )");
            db.Database.ExecuteSqlRaw(
                "INSERT INTO Favorites (Id, Title, Artist, ThumbnailUrl, Duration, UserId) " +
                "SELECT Id, Title, Artist, ThumbnailUrl, Duration, UserId FROM Favorites_old_singlekey");
            db.Database.ExecuteSqlRaw("DROP TABLE Favorites_old_singlekey");
            tx.Commit();
            Console.WriteLine("[Migración] Favoritos: clave primaria cambiada a (Id, UserId).");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Migración] No se pudo migrar Favoritos a clave compuesta: {ex.Message}");
    }

    // Tabla de exclusiones ("no recomendar") — creada aquí porque EnsureCreated
    // no añade tablas nuevas si la BD ya existe.
    try
    {
        db.Database.ExecuteSqlRaw(@"CREATE TABLE IF NOT EXISTS Exclusions (
            Id INTEGER NOT NULL CONSTRAINT PK_Exclusions PRIMARY KEY AUTOINCREMENT,
            UserId TEXT NOT NULL DEFAULT '',
            VideoId TEXT NOT NULL DEFAULT '',
            Artist TEXT NOT NULL DEFAULT '',
            CreatedAt TEXT NOT NULL DEFAULT ''
        )");
    }
    catch { /* ya existe */ }

    // Crear indices para UserId
    string[] indices = {
        "CREATE INDEX IF NOT EXISTS IX_History_UserId ON History(UserId)",
        "CREATE INDEX IF NOT EXISTS IX_Favorites_UserId ON Favorites(UserId)",
        "CREATE INDEX IF NOT EXISTS IX_Playlists_UserId ON Playlists(UserId)",
        "CREATE INDEX IF NOT EXISTS IX_Exclusions_UserId ON Exclusions(UserId)",
    };
    foreach (var sql in indices)
    {
        try { db.Database.ExecuteSqlRaw(sql); }
        catch { /* indice ya existe */ }
    }
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseResponseCompression();
app.UseCors();
app.UseAuthorization();
app.MapControllers();

// Liveness ligero para el watchdog/monitorización (responde al instante, sin tocar
// red externa ni BD). 200 = el servidor está vivo.
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTime.UtcNow }));

app.Run();
