using eMusicApi.Data;
using eMusicApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<MusicExtractionService>();

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
    };
    foreach (var sql in migrations)
    {
        try { db.Database.ExecuteSqlRaw(sql); }
        catch { /* columna ya existe */ }
    }

    // Crear indices para UserId
    string[] indices = {
        "CREATE INDEX IF NOT EXISTS IX_History_UserId ON History(UserId)",
        "CREATE INDEX IF NOT EXISTS IX_Favorites_UserId ON Favorites(UserId)",
        "CREATE INDEX IF NOT EXISTS IX_Playlists_UserId ON Playlists(UserId)",
    };
    foreach (var sql in indices)
    {
        try { db.Database.ExecuteSqlRaw(sql); }
        catch { /* indice ya existe */ }
    }
}

if (app.Environment.IsDevelopment())
    app.MapOpenApi();

app.UseCors();
app.UseAuthorization();
app.MapControllers();
app.Run();
