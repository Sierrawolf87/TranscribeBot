using Microsoft.EntityFrameworkCore;
using TranscribeBot.Models;

namespace TranscribeBot.Data;

public sealed class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
    : DbContext(options)
{
    public DbSet<User> Users { get; set; }
    public DbSet<UserSettings> UserSettings { get; set; }
    public DbSet<Messages> Messages { get; set; }
    public DbSet<AllowedUser> AllowedUsers { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>()
            .HasIndex(user => user.TelegramId)
            .IsUnique();

        modelBuilder.Entity<AllowedUser>()
            .HasIndex(user => user.TelegramId)
            .IsUnique();
    }
}
