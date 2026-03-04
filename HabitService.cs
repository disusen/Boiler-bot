using Microsoft.EntityFrameworkCore;
using ProductivityBot.Data;
using ProductivityBot.Models;

namespace ProductivityBot.Services;

public class HabitService
{
    private readonly BotDbContext _db;

    public HabitService(BotDbContext db) => _db = db;

    public async Task<Habit> AddHabitAsync(ulong userId, string name, string? description = null,
        HabitFrequency frequency = HabitFrequency.Daily)
    {
        var habit = new Habit
        {
            UserId = userId,
            Name = name,
            Description = description,
            Frequency = frequency
        };
        _db.Habits.Add(habit);
        await _db.SaveChangesAsync();
        return habit;
    }

    public async Task<List<Habit>> GetHabitsAsync(ulong userId)
        => await _db.Habits
            .Where(h => h.UserId == userId && h.IsActive)
            .Include(h => h.Logs)
            .OrderBy(h => h.Name)
            .ToListAsync();

    public async Task<Habit?> GetHabitAsync(ulong userId, int habitId)
        => await _db.Habits
            .Include(h => h.Logs)
            .FirstOrDefaultAsync(h => h.Id == habitId && h.UserId == userId);

    /// <summary>
    /// Log a habit completion. Returns false if already logged today.
    /// </summary>
    public async Task<(bool success, string message)> LogHabitAsync(ulong userId, int habitId, string? note = null)
    {
        var habit = await GetHabitAsync(userId, habitId);
        if (habit is null) return (false, "Habit not found.");
        if (!habit.IsActive) return (false, "That habit is archived.");

        var today = DateTime.UtcNow.Date;

        bool alreadyLogged = habit.Logs.Any(l => l.LoggedAt.Date == today);
        if (alreadyLogged)
            return (false, $"You already logged **{habit.Name}** today!");

        var log = new HabitLog
        {
            HabitId = habitId,
            LoggedAt = DateTime.UtcNow,
            Note = note
        };
        _db.HabitLogs.Add(log);
        await _db.SaveChangesAsync();

        // Reload logs for streak calculation
        await _db.Entry(habit).Collection(h => h.Logs).LoadAsync();
        var streak = habit.GetCurrentStreak();

        return (true, streak > 1 ? $"🔥 {streak} day streak!" : "Logged!");
    }

    public async Task<bool> ArchiveHabitAsync(ulong userId, int habitId)
    {
        var habit = await GetHabitAsync(userId, habitId);
        if (habit is null) return false;

        habit.IsActive = false;
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Returns which habits have NOT been logged today.
    /// </summary>
    public async Task<List<Habit>> GetUnloggedTodayAsync(ulong userId)
    {
        var today = DateTime.UtcNow.Date;
        var habits = await GetHabitsAsync(userId);
        return habits.Where(h => !h.Logs.Any(l => l.LoggedAt.Date == today)).ToList();
    }

    public async Task<List<(Habit habit, int streak, int totalLogs)>> GetHabitStatsAsync(ulong userId)
    {
        var habits = await GetHabitsAsync(userId);
        return habits.Select(h => (h, h.GetCurrentStreak(), h.Logs.Count)).ToList();
    }
}
