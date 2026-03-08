using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using ProductivityBot.Data;
using ProductivityBot.Models;

namespace ProductivityBot.Services;

/// <summary>
/// Makes Boiler ramble into a designated channel after a configurable idle period.
/// Each tick of the threshold interval has a 33% chance to post.
/// Rambles build on each other via a history queue — escalation is implicit.
/// Resets fully when !ask is used.
/// </summary>
public class RamblingService
{
    private readonly OllamaService _ollama;
    private readonly IConfiguration _config;
    private readonly ILogger<RamblingService> _logger;
    private readonly IServiceProvider _services;
    private readonly Random _random = new();

    private DiscordSocketClient? _client;
    private Timer? _timer;

    private ulong _channelId;
    private ulong _ownerId;
    private TimeSpan _idleThreshold;

    // Rolling history of this idle session's rambles — feeds back into the next prompt
    // so thoughts build on each other and naturally get weirder over time
    private readonly Queue<(string role, string content)> _rambleHistory = new();
    private const int MaxRambleHistory = 10;

    public RamblingService(OllamaService ollama, IConfiguration config, ILogger<RamblingService> logger, IServiceProvider services)
    {
        _ollama = ollama;
        _config = config;
        _logger = logger;
        _services = services;
    }

    public void Start(DiscordSocketClient client)
    {
        _client = client;

        if (!ulong.TryParse(_config["Bot:RamblingChannelId"], out _channelId) || _channelId == 0)
        {
            _logger.LogWarning("Bot:RamblingChannelId not configured — rambling disabled.");
            return;
        }

        if (!ulong.TryParse(_config["Discord:OwnerId"], out _ownerId) || _ownerId == 0)
        {
            _logger.LogWarning("Discord:OwnerId not configured — rambling disabled.");
            return;
        }

        var thresholdMinutes = double.TryParse(_config["Bot:RamblingIdleThresholdMinutes"], out var m) && m > 0 ? m : 120.0;
        _idleThreshold = TimeSpan.FromMinutes(thresholdMinutes);

        _logger.LogInformation("RamblingService started. Idle threshold: {Threshold}min, channel: {Channel}", thresholdMinutes, _channelId);

        // Timer fires on the same interval as the threshold — each tick is one roll opportunity
        _timer = new Timer(TimerCallback, null, _idleThreshold, _idleThreshold);
    }

    public void Stop() => _timer?.Dispose();

    /// <summary>
    /// Called by OllamaService whenever !ask is used — resets idle clock and clears ramble history.
    /// </summary>
    public void OnInteraction()
    {
        _rambleHistory.Clear();
        _logger.LogDebug("RamblingService: interaction received, history cleared.");
    }

    // -------------------------------------------------------------------------
    //  Timer tick — fires every ThresholdMinutes, 33% roll each time
    // -------------------------------------------------------------------------

    private async void TimerCallback(object? _)
    {
        if (_client is null || !_ollama.IsEnabled) return;

        try
        {
            var idleFor = DateTime.UtcNow - _ollama.LastInteractionAt;

            _logger.LogInformation("RamblingService: tick — idle for {Idle:mm\\:ss}, threshold {Threshold:mm\\:ss}", idleFor, _idleThreshold);

            // Only roll if actually idle — interaction may have reset the clock since last tick
            if (idleFor < _idleThreshold)
            {
                _logger.LogInformation("RamblingService: not idle enough yet, skipping.");
                return;
            }

            var roll = _random.NextDouble();
            _logger.LogInformation("RamblingService: rolled {Roll:F2} — {Result}", roll, roll < 0.33 ? "HIT" : "miss");

            if (roll < 0.33)
                await PostRambleAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in rambling timer callback");
        }
    }

    // -------------------------------------------------------------------------
    //  Ramble generation and posting
    // -------------------------------------------------------------------------

    private async Task PostRambleAsync()
    {
        if (_client is null) return;

        var channel = _client.GetChannel(_channelId) as IMessageChannel;
        if (channel is null)
        {
            _logger.LogWarning("RamblingService: channel {Id} not found.", _channelId);
            return;
        }

        _logger.LogInformation("RamblingService: posting ramble (history depth: {Depth})", _rambleHistory.Count);

        var context = await BuildOwnerContextAsync();
        var messages = new List<(string role, string content)>(_rambleHistory);

        if (messages.Count == 0)
            messages.Add(("user", "What are you thinking about?"));

        await channel.TriggerTypingAsync();
        var ramble = await _ollama.GenerateRambleAsync(messages, context);

        if (string.IsNullOrWhiteSpace(ramble)) return;

        if (_rambleHistory.Count == 0)
            _rambleHistory.Enqueue(("user", "What are you thinking about?"));

        _rambleHistory.Enqueue(("assistant", ramble));

        while (_rambleHistory.Count > MaxRambleHistory)
            _rambleHistory.Dequeue();

        await channel.SendMessageAsync(ramble);
    }

    // -------------------------------------------------------------------------
    //  Owner context — pending tasks and unlogged habits
    // -------------------------------------------------------------------------

    private async Task<string> BuildOwnerContextAsync()
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var now = DateTime.UtcNow;
            var today = now.Date;

            var pendingTasks = await db.Tasks
                .Where(t => t.UserId == _ownerId && !t.IsCompleted)
                .OrderBy(t => t.Priority == TaskPriority.High ? 0 : t.Priority == TaskPriority.Normal ? 1 : 2)
                .Take(5)
                .ToListAsync();

            var activeHabits = await db.Habits
                .Where(h => h.UserId == _ownerId && h.IsActive)
                .ToListAsync();

            var loggedTodayIds = await db.HabitLogs
                .Where(l => activeHabits.Select(h => h.Id).Contains(l.HabitId) && l.LoggedAt >= today)
                .Select(l => l.HabitId)
                .Distinct()
                .ToListAsync();

            var unloggedHabits = activeHabits
                .Where(h => !loggedTodayIds.Contains(h.Id))
                .ToList();

            if (!pendingTasks.Any() && !unloggedHabits.Any())
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You are passively aware of the following about your owner's day — you may or may not reference these, don't force it:");

            if (pendingTasks.Any())
            {
                sb.AppendLine("Pending tasks:");
                foreach (var t in pendingTasks)
                {
                    var overdue = t.DueDate.HasValue && t.DueDate < now ? " (overdue)" : "";
                    sb.AppendLine($"  - {t.Title} [{t.Priority}]{overdue}");
                }
            }

            if (unloggedHabits.Any())
            {
                sb.AppendLine("Habits not yet logged today:");
                foreach (var h in unloggedHabits)
                    sb.AppendLine($"  - {h.Name}");
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RamblingService: failed to build owner context");
            return string.Empty;
        }
    }
}