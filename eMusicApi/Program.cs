using eMusicApi.Data;
using eMusicApi.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();
builder.Services.AddMemoryCache();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=emusic.db"));

builder.Services.AddHttpClient<PipedApiService>(client =>
{
    // En Docker (Pi): usar la red interna. En desarrollo: usar el dominio externo.
    var pipedUrl = Environment.GetEnvironmentVariable("PIPED_API_URL") 
                   ?? "https://api.emusicmp3.duckdns.org";
    client.BaseAddress = new Uri(pipedUrl);
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.Run();
