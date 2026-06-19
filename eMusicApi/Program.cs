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

    // Agregar columnas si no existen (SQLite no las agrega con EnsureCreated si la tabla ya existe)
    string[] migrations = {
        "ALTER TABLE History ADD COLUMN PlayCount INTEGER NOT NULL DEFAULT 1",
        "ALTER TABLE History ADD COLUMN UserId TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE Favorites ADD COLUMN UserId TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE Playlists ADD COLUMN UserId TEXT NOT NULL DEFAULT ''",
        "ALTER TABLE History ADD COLUMN SkippedEarly INTEGER NOT NULL DEFAULT 0",
        "ALTER TABLE History ADD COLUMN IsDownloaded INTEGER NOT NULL DEFAULT 0",
    };
    foreach (var sql in migrations)
    {
        try { db.Database.ExecuteSqlRaw(sql); }
        catch { /* columna ya existe */ }
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
