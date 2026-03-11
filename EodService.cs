using Discord;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProductivityBot.Data;
using ProductivityBot.Models;

namespace ProductivityBot.Services;

/// <summary>
/// Manages the end-of-day summary feature.
/// Fires automatically at 9pm in the configured timezone, or on demand via !eod.
/// After generating the summary, runs a reflection pass that writes EOD insights
/// back into the memory layer so Boiler carries them forward.
/// </summary>
public class EodService
{
    private readonly IServiceProvider _services;
    private readonly OllamaService _ollama;
    private readonly IConfiguration _config;
    private readonly ILogger<EodService> _logger;

    // Injected lazily — PersonalityService depends on EodService indirectly
    private MemoryService? _memoryService;
    private PersonalityService? _personalityService;

    public void SetMemoryService(MemoryService memoryService) => _memoryService = memoryService;
    public void SetPersonalityService(PersonalityService personalityService) => _personalityService = personalityService;

    private DiscordSocketClient? _client;
    private Timer? _timer;

    private TimeZoneInfo _timezone = TimeZoneInfo.Utc;
    private ulong _ownerId;
    private bool _ownerIdValid;

    public EodService(
        IServiceProvider services,
        OllamaService ollama,
        IConfiguration config,
        ILogger<EodService> logger)
    {
        _services = services;
        _ollama = ollama;
        _config = config;
        _logger = logger;
    }

    public void Start(DiscordSocketClient client)
    {
        _client = client;

        var tzId = _config["Bot:Timezone"] ?? "UTC";
        try
        {
            _timezone = TimeZoneInfo.FindSystemTimeZoneById(tzId);
        }
        catch
        {
            _logger.LogWarning("Unknown timezone '{TzId}' — falling back to UTC.", tzId);
            _timezone = TimeZoneInfo.Utc;
        }

        _ownerIdValid = ulong.TryParse(_config["Discord:OwnerId"], out _ownerId);
        if (!_ownerIdValid)
            _logger.LogWarning("Discord:OwnerId not configured — EOD auto-trigger will not fire.");

        _timer = new Timer(TimerCallback, null, TimeSpan.Zero, TimeSpan.FromMinutes(1));
        _logger.LogInformation("EodService started. Timezone: {Tz}", _timezone.Id);
    }

    public void Stop() => _timer?.Dispose();

    // -------------------------------------------------------------------------
    //  Timer tick
    // -------------------------------------------------------------------------

    private async void TimerCallback(object? _)
    {
        if (!_ollama.IsEnabled || !_ownerIdValid || _client is null) return;

        try
        {
            var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timezone);
            var todayLocal = DateOnly.FromDateTime(nowLocal);

            if (nowLocal.Hour != 21 || nowLocal.Minute != 0) return;

            await TryFireEodAsync(todayLocal, firedByCommand: false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in EOD timer callback");
        }
    }

    // -------------------------------------------------------------------------
    //  Public entry point for !eod command
    // -------------------------------------------------------------------------

    public async Task<string?> TriggerManualEodAsync()
    {
        if (!_ollama.IsEnabled)
            return "🚫 EOD is disabled because no Ollama model is active. Restart the bot to configure one.";

        if (_client is null)
            return "❌ Bot client is not ready yet.";

        var nowLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timezone);
        var todayLocal = DateOnly.FromDateTime(nowLocal);

        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        bool alreadyFired = await db.EodLogs.AnyAsync(e => e.Date == todayLocal);
        if (alreadyFired)
            return $"⚠️ EOD has already been sent today ({todayLocal:MMM d}). It resets at midnight {_timezone.Id} time.";

        _ = Task.Run(() => TryFireEodAsync(todayLocal, firedByCommand: true));
        return null;
    }

    // -------------------------------------------------------------------------
    //  Core fire logic
    // -------------------------------------------------------------------------

    private async Task TryFireEodAsync(DateOnly date, bool firedByCommand)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        bool alreadyFired = await db.EodLogs.AnyAsync(e => e.Date == date);
        if (alreadyFired) return;

        db.EodLogs.Add(new EodLog
        {
            Date = date,
            FiredByCommand = firedByCommand,
            FiredAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        _logger.LogInformation("Firing EOD for {Date} (byCommand={ByCommand})", date, firedByCommand);

        try
        {
            var (summaryText, eodSignal) = await BuildAndGenerateSummaryAsync(db, date);
            await SendSummaryDmAsync(summaryText, date);

            // Reflection pass — write EOD insights into memory
            await RunEodReflectionAsync(eodSignal, date);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build or send EOD summary for {Date}", date);
        }
    }

    // -------------------------------------------------------------------------
    //  Data collection + AI generation
    // -------------------------------------------------------------------------

    private async Task<(string summaryText, EodSignal signal)> BuildAndGenerateSummaryAsync(BotDbContext db, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var dayEnd   = date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Unspecified);

        var dayStartUtc = TimeZoneInfo.ConvertTimeToUtc(dayStart, _timezone);
        var dayEndUtc   = TimeZoneInfo.ConvertTimeToUtc(dayEnd,   _timezone);

        // --- Tasks ---
        var completedToday = await db.Tasks
            .Where(t => t.UserId == _ownerId
                     && t.IsCompleted
                     && t.CompletedAt >= dayStartUtc
                     && t.CompletedAt <= dayEndUtc)
            .ToListAsync();

        var addedToday = await db.Tasks
            .Where(t => t.UserId == _ownerId
                     && t.CreatedAt >= dayStartUtc
                     && t.CreatedAt <= dayEndUtc)
            .ToListAsync();

        var stillPending = await db.Tasks
            .Where(t => t.UserId == _ownerId && !t.IsCompleted)
            .OrderBy(t => t.DueDate ?? DateTime.MaxValue)
            .ToListAsync();

        // --- Habits ---
        var allHabits = await db.Habits
            .Where(h => h.UserId == _ownerId && h.IsActive)
            .Include(h => h.Logs)
            .ToListAsync();

        var loggedToday  = allHabits.Where(h => h.Logs.Any(l => l.LoggedAt >= dayStartUtc && l.LoggedAt <= dayEndUtc)).ToList();
        var missedToday  = allHabits.Where(h => h.Logs.All(l => l.LoggedAt < dayStartUtc || l.LoggedAt > dayEndUtc)).ToList();

        // Build signal for reflection pass
        var signal = new EodSignal
        {
            Date = date,
            TasksCompleted = completedToday.Count,
            TasksAdded = addedToday.Count,
            TasksStillPending = stillPending.Count,
            HabitsLogged = loggedToday.Count,
            HabitsMissed = missedToday.Count,
            TotalHabits = allHabits.Count,
            MissedHabitNames = missedToday.Select(h => h.Name).ToList(),
            CompletedTaskTitles = completedToday.Select(t => t.Title).ToList()
        };

        // --- Build data string ---
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"DATE: {date:dddd, MMMM d yyyy}");
        sb.AppendLine();

        sb.AppendLine("=== TASKS ===");
        sb.AppendLine($"Completed today ({completedToday.Count}):");
        if (completedToday.Count == 0)
            sb.AppendLine("  (none)");
        else
            foreach (var t in completedToday)
                sb.AppendLine($"  ✅ [{t.Priority}] {t.Title}");

        sb.AppendLine($"Added today ({addedToday.Count}):");
        if (addedToday.Count == 0)
            sb.AppendLine("  (none)");
        else
            foreach (var t in addedToday)
                sb.AppendLine($"  ➕ [{t.Priority}] {t.Title}{(t.IsCompleted ? " (also completed today)" : "")}");

        sb.AppendLine($"Still pending ({stillPending.Count}):");
        if (stillPending.Count == 0)
            sb.AppendLine("  (none — inbox zero!)");
        else
            foreach (var t in stillPending.Take(10))
            {
                var overdue = t.DueDate.HasValue && t.DueDate < DateTime.UtcNow ? " ⚠️ OVERDUE" : "";
                sb.AppendLine($"  ⬜ [{t.Priority}] {t.Title}{overdue}");
            }

        sb.AppendLine();
        sb.AppendLine("=== HABITS ===");
        sb.AppendLine($"Logged today ({loggedToday.Count}/{allHabits.Count}):");
        if (loggedToday.Count == 0)
            sb.AppendLine("  (none)");
        else
            foreach (var h in loggedToday)
            {
                var streak = h.GetCurrentStreak();
                sb.AppendLine($"  ✅ {h.Name}{(streak > 1 ? $" — {streak} day streak" : "")}");
            }

        sb.AppendLine($"Missed today ({missedToday.Count}):");
        if (missedToday.Count == 0)
            sb.AppendLine("  (none — perfect day!)");
        else
            foreach (var h in missedToday)
                sb.AppendLine($"  ❌ {h.Name}");

        var dataString = sb.ToString();
        _logger.LogDebug("EOD data payload:\n{Data}", dataString);

        var summaryText = await _ollama.GenerateEodSummaryAsync(dataString);
        return (summaryText, signal);
    }

    // -------------------------------------------------------------------------
    //  EOD reflection — writes insights into the memory layer
    // -------------------------------------------------------------------------

    private async Task RunEodReflectionAsync(EodSignal signal, DateOnly date)
    {
        if (_memoryService is null) return;

        try
        {
            // Determine the day's overall quality for streak tracking
            var habitCompletionRate = signal.TotalHabits > 0
                ? (float)signal.HabitsLogged / signal.TotalHabits
                : 1.0f;

            var dayWasGood = signal.TasksCompleted >= 1 && habitCompletionRate >= 0.7f;
            var dayWasRough = signal.TasksCompleted == 0 && signal.HabitsMissed > 0;

            // Determine valence for the memory
            var valence = dayWasGood ? EmotionalValence.Positive
                        : dayWasRough ? EmotionalValence.Negative
                        : EmotionalValence.Neutral;

            // Importance scales with how notable the day was
            var importance = dayWasGood || dayWasRough ? 0.7f : 0.4f;

            // Build a terse memory of this EOD
            var memoryParts = new List<string> { $"EOD {date:MMM d}:" };

            if (signal.TasksCompleted > 0)
                memoryParts.Add($"completed {signal.TasksCompleted} task(s)");

            if (signal.HabitsLogged == signal.TotalHabits && signal.TotalHabits > 0)
                memoryParts.Add("logged all habits");
            else if (signal.HabitsMissed > 0)
                memoryParts.Add($"missed {string.Join(", ", signal.MissedHabitNames)}");

            if (signal.TasksStillPending > 3)
                memoryParts.Add($"{signal.TasksStillPending} tasks still pending");

            var memoryContent = string.Join("; ", memoryParts);
            await _memoryService.AddMemoryAsync(_ownerId, memoryContent, valence, importance, tag: "eod");

            // Update consecutive day streaks in BotState
            await _memoryService.UpdateStateAsync(_ownerId, s =>
            {
                if (dayWasGood)
                {
                    s.ConsecutiveGoodDays++;
                    s.ConsecutiveRoughDays = 0;
                }
                else if (dayWasRough)
                {
                    s.ConsecutiveRoughDays++;
                    s.ConsecutiveGoodDays = 0;
                }
                else
                {
                    // Mixed day — reset both streaks
                    s.ConsecutiveGoodDays = 0;
                    s.ConsecutiveRoughDays = 0;
                }

                // Update mood signal based on trend
                s.RecentObservation = dayWasGood
                    ? $"Had a productive day on {date:MMM d} — completed {signal.TasksCompleted} task(s) and logged most habits."
                    : dayWasRough
                    ? $"Rough day on {date:MMM d} — no tasks completed and missed some habits ({string.Join(", ", signal.MissedHabitNames)})."
                    : $"Mixed day on {date:MMM d}.";
            });

            // If there's a rough streak building, create a goal to gently follow up
            var state = await _memoryService.GetOrCreateStateAsync(_ownerId);
            if (state.ConsecutiveRoughDays >= 3)
            {
                await _memoryService.AddGoalAsync(
                    _ownerId,
                    $"Check in with Gvidas — {state.ConsecutiveRoughDays} rough days in a row. Be gentle, not pushy.",
                    reason: "Consecutive rough EODs detected",
                    priority: 8,
                    surfaceAfter: DateTime.UtcNow);
            }

            // Trigger a personality reflection after EOD so BotState updates with fresh context
            if (_personalityService is not null)
                _ = Task.Run(() => _personalityService.RunReflectionAsync(_ownerId));

            _logger.LogInformation("EOD reflection stored for {Date}. Valence: {Valence}, Good streak: {Good}, Rough streak: {Rough}",
                date, valence, state.ConsecutiveGoodDays, state.ConsecutiveRoughDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "EodService: reflection pass failed for {Date}", date);
        }
    }

    // -------------------------------------------------------------------------
    //  DM delivery
    // -------------------------------------------------------------------------

    private async Task SendSummaryDmAsync(string summary, DateOnly date)
    {
        if (_client is null) return;

        IUser? owner;
        try
        {
            owner = await _client.GetUserAsync(_ownerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not fetch owner user for EOD DM");
            return;
        }

        if (owner is null)
        {
            _logger.LogWarning("Owner user {Id} not found — EOD DM not sent.", _ownerId);
            return;
        }

        var dmChannel = await owner.CreateDMChannelAsync();

        const int maxLen = 3800;
        var chunks = SplitText(summary, maxLen);

        for (int i = 0; i < chunks.Count; i++)
        {
            var embed = new EmbedBuilder()
                .WithColor(new Color(0x7289DA))
                .WithTitle(i == 0 ? $"🌙 End of Day — {date:MMM d, yyyy}" : "🌙 End of Day (continued)")
                .WithDescription(chunks[i])
                .WithFooter(i == chunks.Count - 1
                    ? $"Boiler • {_timezone.Id}"
                    : $"Part {i + 1}/{chunks.Count}")
                .WithTimestamp(DateTimeOffset.UtcNow)
                .Build();

            await dmChannel.SendMessageAsync(embed: embed);
        }
    }

    // -------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------

    private static List<string> SplitText(string text, int maxLength)
    {
        var chunks = new List<string>();
        int index = 0;
        while (index < text.Length)
        {
            int length = Math.Min(maxLength, text.Length - index);
            if (index + length < text.Length)
            {
                int splitAt = text.LastIndexOf('\n', index + length, length);
                if (splitAt == -1) splitAt = text.LastIndexOf(' ', index + length, length);
                if (splitAt > index) length = splitAt - index;
            }
            chunks.Add(text.Substring(index, length).Trim());
            index += length;
        }
        return chunks;
    }

    // -------------------------------------------------------------------------
    //  Internal signal type — passed from summary builder to reflection pass
    // -------------------------------------------------------------------------

    private class EodSignal
    {
        public DateOnly Date { get; set; }
        public int TasksCompleted { get; set; }
        public int TasksAdded { get; set; }
        public int TasksStillPending { get; set; }
        public int HabitsLogged { get; set; }
        public int HabitsMissed { get; set; }
        public int TotalHabits { get; set; }
        public List<string> MissedHabitNames { get; set; } = new();
        public List<string> CompletedTaskTitles { get; set; } = new();
    }
}
