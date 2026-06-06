using eMusicApi.Models;
using Microsoft.EntityFrameworkCore;

namespace eMusicApi.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Favorite> Favorites { get; set; }
    public DbSet<History> History { get; set; }
    public DbSet<Playlist> Playlists { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<History>().HasIndex(h => h.UserId);
        modelBuilder.Entity<Favorite>().HasIndex(f => f.UserId);
        modelBuilder.Entity<Playlist>().HasIndex(p => p.UserId);
    }
}
