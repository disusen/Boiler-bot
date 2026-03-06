using Microsoft.EntityFrameworkCore;
using ProductivityBot.Models;

namespace ProductivityBot.Data;

public class BotDbContext : DbContext
{
    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options) { }

    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Habit> Habits => Set<Habit>();
    public DbSet<HabitLog> HabitLogs => Set<HabitLog>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<EodLog> EodLogs => Set<EodLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TaskItem>(e =>
        {
            e.HasKey(t => t.Id);
            e.HasIndex(t => t.UserId);
            e.Property(t => t.Title).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<Habit>(e =>
        {
            e.HasKey(h => h.Id);
            e.HasIndex(h => h.UserId);
            e.Property(h => h.Name).HasMaxLength(100).IsRequired();
            e.HasMany(h => h.Logs)
             .WithOne(l => l.Habit)
             .HasForeignKey(l => l.HabitId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<HabitLog>(e =>
        {
            e.HasKey(l => l.Id);
            e.HasIndex(l => new { l.HabitId, l.LoggedAt });
        });

        modelBuilder.Entity<Reminder>(e =>
        {
            e.HasKey(r => r.Id);
            e.HasIndex(r => new { r.UserId, r.IsFired });
        });

        modelBuilder.Entity<EodLog>(e =>
        {
            e.HasKey(el => el.Id);
            // One EOD per calendar date — enforced in service logic
            e.HasIndex(el => el.Date).IsUnique();
        });
    }
}
