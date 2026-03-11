using Microsoft.EntityFrameworkCore;
using ProductivityBot.Models;

namespace ProductivityBot.Data;

public class BotDbContext : DbContext
{
    public BotDbContext(DbContextOptions<BotDbContext> options) : base(options) { }

    // --- Existing tables ---
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<Habit> Habits => Set<Habit>();
    public DbSet<HabitLog> HabitLogs => Set<HabitLog>();
    public DbSet<Reminder> Reminders => Set<Reminder>();
    public DbSet<EodLog> EodLogs => Set<EodLog>();

    // --- Companion memory layer ---
    public DbSet<BotMemory> Memories => Set<BotMemory>();
    public DbSet<BotFact> Facts => Set<BotFact>();
    public DbSet<BotState> BotStates => Set<BotState>();
    public DbSet<BotGoal> Goals => Set<BotGoal>();
    public DbSet<BotBelief> Beliefs => Set<BotBelief>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        // ---- Existing ----

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
            e.HasIndex(el => el.Date).IsUnique();
        });

        // ---- Companion memory layer ----

        modelBuilder.Entity<BotMemory>(e =>
        {
            e.HasKey(m => m.Id);
            // Primary retrieval path: user's recent important memories
            e.HasIndex(m => new { m.UserId, m.Importance, m.OccurredAt });
            e.HasIndex(m => new { m.UserId, m.Tag });
            e.Property(m => m.Content).HasMaxLength(1000).IsRequired();
            e.Property(m => m.Tag).HasMaxLength(50);
        });

        modelBuilder.Entity<BotFact>(e =>
        {
            e.HasKey(f => f.Id);
            e.HasIndex(f => new { f.UserId, f.Category });
            // FactKey uniqueness per user — used for upsert-style updates
            e.HasIndex(f => new { f.UserId, f.FactKey })
             .IsUnique()
             .HasFilter("[FactKey] IS NOT NULL");
            e.Property(f => f.Content).HasMaxLength(500).IsRequired();
            e.Property(f => f.FactKey).HasMaxLength(100);
        });

        modelBuilder.Entity<BotState>(e =>
        {
            e.HasKey(s => s.Id);
            // One state row per user
            e.HasIndex(s => s.UserId).IsUnique();
            e.Property(s => s.CurrentMood).HasMaxLength(200);
            e.Property(s => s.CurrentThought).HasMaxLength(500);
            e.Property(s => s.RecentObservation).HasMaxLength(1000);
        });

        modelBuilder.Entity<BotGoal>(e =>
        {
            e.HasKey(g => g.Id);
            e.HasIndex(g => new { g.UserId, g.Status });
            e.Property(g => g.Description).HasMaxLength(500).IsRequired();
            e.Property(g => g.Reason).HasMaxLength(500);
            e.Property(g => g.Notes).HasMaxLength(1000);
        });

        modelBuilder.Entity<BotBelief>(e =>
        {
            e.HasKey(b => b.Id);
            // Primary lookup: user's active beliefs
            e.HasIndex(b => new { b.UserId, b.Status });
            // BeliefKey uniqueness per user — used for confirm/contradict lookups
            e.HasIndex(b => new { b.UserId, b.BeliefKey }).IsUnique();
            e.Property(b => b.Claim).HasMaxLength(500).IsRequired();
            e.Property(b => b.BeliefKey).HasMaxLength(100).IsRequired();
            e.Property(b => b.BehavioralImplication).HasMaxLength(500);
            e.Property(b => b.FormationEvidence).HasMaxLength(1000);
        });
    }
}
