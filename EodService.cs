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
/// Requires Ollama to be enabled — if disabled, EOD is fully inactive.
/// </summary>
public class EodService
{
    private readonly IServiceProvider _services;
    private readonly OllamaService _ollama;
    private readonly IConfiguration _config;
    private readonly ILogger<EodService> _logger;

    private DiscordSocketClient? _client;
    private Timer? _timer;

    // Resolved once at Start() so we don't re-parse every tick
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

        // Resolve timezone from config, e.g. "Europe/Vilnius" or "Eastern Standard Time"
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

        // Check every minute whether it's time to fire
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

            // Fire window: 21:00–21:01 (we check every minute, so one tick lands in this window)
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

    /// <summary>
    /// Called by EodCommands. Returns a user-facing error string, or null on success.
    /// </summary>
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

        // Fire asynchronously so the command can ack immediately
        _ = Task.Run(() => TryFireEodAsync(todayLocal, firedByCommand: true));
        return null; // success — caller sends the ack
    }

    // -------------------------------------------------------------------------
    //  Core fire logic
    // -------------------------------------------------------------------------

    private async Task TryFireEodAsync(DateOnly date, bool firedByCommand)
    {
        // Double-check inside a scope to guard against races (e.g. manual + timer at 21:00)
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        bool alreadyFired = await db.EodLogs.AnyAsync(e => e.Date == date);
        if (alreadyFired) return;

        // Mark as fired immediately to prevent re-entry
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
            var summaryText = await BuildAndGenerateSummaryAsync(db, date);
            await SendSummaryDmAsync(summaryText, date);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to build or send EOD summary for {Date}", date);
        }
    }

    // -------------------------------------------------------------------------
    //  Data collection + AI generation
    // -------------------------------------------------------------------------

    private async Task<string> BuildAndGenerateSummaryAsync(BotDbContext db, DateOnly date)
    {
        var dayStart = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        var dayEnd   = date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Unspecified);

        // Convert the local day bounds to UTC for DB queries
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

        // --- Build structured data string for Ollama ---
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
            foreach (var t in stillPending.Take(10)) // cap at 10 to keep prompt sane
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

        return await _ollama.GenerateEodSummaryAsync(dataString);
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

        // Split if over Discord's 4096 embed description limit
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
}
