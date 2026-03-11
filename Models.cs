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

public class EodLog
{
    public int Id { get; set; }
    public DateOnly Date { get; set; }
    public bool FiredByCommand { get; set; }
    public DateTime FiredAt { get; set; } = DateTime.UtcNow;
}

// ---------------------------------------------------------------------------
//  Companion memory layer
// ---------------------------------------------------------------------------

/// <summary>
/// A single remembered moment — something that happened, was said, or was observed.
/// Episodic in nature: tied to a point in time, carries emotional weight.
/// Examples: "Gvidas told me he's stressed about the RoadWise deadline"
///           "Gvidas completed 5 tasks today — really productive"
/// </summary>
public class BotMemory
{
    public int Id { get; set; }
    public ulong UserId { get; set; }

    /// <summary>What was observed or experienced.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Rough emotional tone of the memory.
    /// Positive = good news, achievement, upbeat mood.
    /// Negative = stress, failure, worry.
    /// Neutral  = factual, informational.
    /// </summary>
    public EmotionalValence Valence { get; set; } = EmotionalValence.Neutral;

    /// <summary>
    /// How important this memory is. 0.0–1.0.
    /// High importance memories are always included in prompts.
    /// Low importance memories decay and eventually get pruned.
    /// </summary>
    public float Importance { get; set; } = 0.5f;

    /// <summary>How many times this memory has been surfaced in a prompt. Used for relevance boosting.</summary>
    public int TimesReferenced { get; set; } = 0;

    /// <summary>UTC timestamp of when this happened.</summary>
    public DateTime OccurredAt { get; set; } = DateTime.UtcNow;

    /// <summary>Last time this was included in a prompt context.</summary>
    public DateTime? LastReferencedAt { get; set; }

    /// <summary>Optional tag for grouping: "task", "habit", "conversation", "eod", "observation".</summary>
    public string? Tag { get; set; }
}

public enum EmotionalValence
{
    Negative = -1,
    Neutral = 0,
    Positive = 1
}

/// <summary>
/// A persistent fact about the user — stable knowledge that doesn't expire quickly.
/// Semantic in nature: what Boiler "knows" about you as a person.
/// Examples: "Gvidas is a CS student at KTU"
///           "Gvidas tends to fall off habits when university load spikes"
///           "Gvidas has a beagle named Boiler"
/// </summary>
public class BotFact
{
    public int Id { get; set; }
    public ulong UserId { get; set; }

    /// <summary>The fact itself, in plain language.</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>
    /// Confidence in this fact. 0.0–1.0.
    /// Observed directly = 0.9+. Inferred = 0.5–0.8. Speculative = below 0.5.
    /// </summary>
    public float Confidence { get; set; } = 0.8f;

    /// <summary>Category for grouping and prompt selection.</summary>
    public FactCategory Category { get; set; } = FactCategory.General;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// If true, this fact was explicitly told to Boiler by the user (higher trust).
    /// If false, it was inferred by Boiler from conversation.
    /// </summary>
    public bool UserProvided { get; set; } = false;

    /// <summary>
    /// If set, this fact supersedes an older one with the same key.
    /// Used to update facts without leaving stale data (e.g. "lives in Kaunas" → "lives in Vilnius").
    /// </summary>
    public string? FactKey { get; set; }
}

public enum FactCategory
{
    General = 0,
    Personal = 1,      // name, location, life situation
    Work = 2,          // job, projects, university
    Health = 3,        // physical / mental health patterns
    Patterns = 4,      // behavioural tendencies Boiler has observed
    Preferences = 5,   // likes, dislikes, communication style
    Goals = 6          // long-term aspirations mentioned in conversation
}

/// <summary>
/// Boiler's internal state — its current mood, what it's thinking about, and its beliefs
/// about the user. Persisted so it survives restarts.
/// There is exactly one BotState row per user.
/// </summary>
public class BotState
{
    public int Id { get; set; }
    public ulong UserId { get; set; }

    /// <summary>Boiler's current emotional state, in plain language for prompt injection.</summary>
    public string CurrentMood { get; set; } = "content and curious";

    /// <summary>
    /// What Boiler is currently "thinking about" — the thread it carries between sessions.
    /// Gets updated by the heartbeat reflection pass.
    /// Example: "wondering if Gvidas is managing his workload okay this week"
    /// </summary>
    public string? CurrentThought { get; set; }

    /// <summary>
    /// Rolling summary of recent observations about the user's wellbeing and productivity.
    /// Updated by EOD reflection. Feeds into ramble generation and general tone.
    /// </summary>
    public string? RecentObservation { get; set; }

    /// <summary>
    /// How many consecutive days have had a positive EOD signal.
    /// Used to modulate tone — after 3+ good days Boiler is more upbeat; after 3+ rough days, more gentle.
    /// </summary>
    public int ConsecutiveGoodDays { get; set; } = 0;
    public int ConsecutiveRoughDays { get; set; } = 0;

    public DateTime LastUpdatedAt { get; set; } = DateTime.UtcNow;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Something Boiler is actively tracking on behalf of the user — not a user-set reminder,
/// but something Boiler decided mattered based on context.
/// Examples: "Follow up on whether Gvidas checked in on his weight"
///           "Ask about RoadWise demo outcome next time he's around"
/// </summary>
public class BotGoal
{
    public int Id { get; set; }
    public ulong UserId { get; set; }

    /// <summary>What Boiler intends to do or track.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>Why Boiler created this goal — for transparency and introspection.</summary>
    public string? Reason { get; set; }

    public BotGoalStatus Status { get; set; } = BotGoalStatus.Active;

    /// <summary>
    /// Priority 0–10. Higher = more likely to be surfaced in rambles and EOD.
    /// </summary>
    public int Priority { get; set; } = 5;

    /// <summary>
    /// If set, Boiler should surface this goal around this time.
    /// Not a hard reminder — just a soft nudge window.
    /// </summary>
    public DateTime? SurfaceAfter { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Optional notes Boiler has added while pursuing this goal.</summary>
    public string? Notes { get; set; }
}

public enum BotGoalStatus
{
    Active = 0,
    Resolved = 1,
    Abandoned = 2
}
