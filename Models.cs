namespace ProductivityBot.Models;

public class TaskItem
{
    public int Id { get; set; }
    public ulong UserId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsCompleted { get; set; }
    public DateTime? DueDate { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
    public TaskPriority Priority { get; set; } = TaskPriority.Normal;
}

public enum TaskPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

public class Habit
{
    public int Id { get; set; }
    public ulong UserId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public HabitFrequency Frequency { get; set; } = HabitFrequency.Daily;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<HabitLog> Logs { get; set; } = new();

    // Computed: streak (days in a row completed for daily habits)
    public int GetCurrentStreak()
    {
        if (Frequency != HabitFrequency.Daily || Logs.Count == 0) return 0;

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var logDates = Logs
            .Select(l => DateOnly.FromDateTime(l.LoggedAt))
            .Distinct()
            .OrderByDescending(d => d)
            .ToList();

        int streak = 0;
        var expected = today;

        foreach (var date in logDates)
        {
            if (date == expected)
            {
                streak++;
                expected = expected.AddDays(-1);
            }
            else if (date < expected)
            {
                break;
            }
        }

        return streak;
    }
}

public enum HabitFrequency
{
    Daily = 0,
    Weekly = 1
}

public class HabitLog
{
    public int Id { get; set; }
    public int HabitId { get; set; }
    public Habit Habit { get; set; } = null!;
    public DateTime LoggedAt { get; set; } = DateTime.UtcNow;
    public string? Note { get; set; }
}

public class Reminder
{
    public int Id { get; set; }
    public ulong UserId { get; set; }
    public ulong ChannelId { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTime FireAt { get; set; }
    public bool IsFired { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
