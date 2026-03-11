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
/// Decides when Boiler should proactively DM the user, composes the message,
/// and enforces cooldowns so outreach feels considered rather than spammy.
///
/// Called by PersonalityService after each heartbeat reflection pass.
///
/// Cooldown rules:
///   - Same trigger type cannot fire within its cooldown window
///   - Max 2 DMs per day total regardless of triggers
///   - Never between midnight and 8am in the configured timezone
///
/// Trigger priority (highest fires first if multiple qualify):
///   1. ConsecutiveRoughDays  ≥3         cooldown 48h
///   2. HighPriorityGoal      unresolved 48h+   cooldown 24h
///   3. GoalSurfaceTime       passed             cooldown 12h
///   4. OverdueTask           high priority 24h+ cooldown 24h
///   5. SilentWithThought     48h silent + pending high-priority thought  cooldown 36h
///   6. BeliefCrossedActive   first time belief goes active   cooldown 72h (rare, meaningful)
/// </summary>
public class OutreachService
{
    private readonly IServiceProvider _services;
    private readonly OllamaService _ollama;
    private readonly MemoryService _memory;
    private readonly IConfiguration _config;
    private readonly ILogger<OutreachService> _logger;

    private DiscordSocketClient? _client;
    private ulong _ownerId;
    private TimeZoneInfo _timezone = TimeZoneInfo.Utc;

    // Daily DM cap
    private const int MaxDmsPerDay = 2;

    // Trigger cooldowns
    private static readonly Dictionary<OutreachTrigger, TimeSpan> Cooldowns = new()
    {
        [OutreachTrigger.ConsecutiveRoughDays]  = TimeSpan.FromHours(48),
        [OutreachTrigger.HighPriorityGoal]      = TimeSpan.FromHours(24),
        [OutreachTrigger.GoalSurfaceTime]       = TimeSpan.FromHours(12),
        [OutreachTrigger.OverdueTask]           = TimeSpan.FromHours(24),
        [OutreachTrigger.SilentWithThought]     = TimeSpan.FromHours(36),
        [OutreachTrigger.BeliefCrossedActive]   = TimeSpan.FromHours(72),
    };

    public OutreachService(
        IServiceProvider services,
        OllamaService ollama,
        MemoryService memory,
        IConfiguration config,
        ILogger<OutreachService> logger)
    {
        _services = services;
        _ollama = ollama;
        _memory = memory;
        _config = config;
        _logger = logger;
    }

    public void Initialize(DiscordSocketClient client)
    {
        _client = client;

        ulong.TryParse(_config["Discord:OwnerId"], out _ownerId);

        var tzId = _config["Bot:Timezone"] ?? "UTC";
        try { _timezone = TimeZoneInfo.FindSystemTimeZoneById(tzId); }
        catch { _timezone = TimeZoneInfo.Utc; }
    }

    // -------------------------------------------------------------------------
    //  Main evaluation — called by PersonalityService after heartbeat reflection
    // -------------------------------------------------------------------------

    /// <summary>
    /// Evaluates all outreach conditions in priority order.
    /// Fires at most one DM per call. Returns true if a DM was sent.
    /// </summary>
    public async Task<bool> EvaluateAndReachOutAsync(ulong userId)
    {
        if (_client is null || !_ollama.IsEnabled) return false;

        try
        {
            // Time gate — never DM during quiet hours (midnight–8am local)
            var localNow = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, _timezone);
            if (localNow.Hour < 8)
            {
                _logger.LogDebug("OutreachService: quiet hours ({Hour}:xx local), skipping evaluation.", localNow.Hour);
                return false;
            }

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            // Daily cap check
            var todayStart = DateTime.UtcNow.Date;
            var dmsToday = await db.OutreachLogs
                .CountAsync(o => o.UserId == userId && o.SentAt >= todayStart);

            if (dmsToday >= MaxDmsPerDay)
            {
                _logger.LogDebug("OutreachService: daily cap reached ({Count}/{Max}).", dmsToday, MaxDmsPerDay);
                return false;
            }

            // Evaluate triggers in priority order
            var (trigger, context) = await FindHighestPriorityTriggerAsync(db, userId);

            if (trigger is null)
            {
                _logger.LogDebug("OutreachService: no triggers qualified.");
                return false;
            }

            _logger.LogInformation("OutreachService: trigger qualified — {Trigger}", trigger.Value);

            // Compose the message
            var state = await _memory.GetOrCreateStateAsync(userId);
            var memoryContext = await _memory.HydrateAsync(userId);
            var message = await _ollama.ComposeOutreachMessageAsync(trigger.Value, context, state, memoryContext);

            if (string.IsNullOrWhiteSpace(message)) return false;

            // Send the DM
            var sent = await SendDmAsync(userId, message);
            if (!sent) return false;

            // Log the outreach
            db.OutreachLogs.Add(new OutreachLog
            {
                UserId = userId,
                Trigger = trigger.Value,
                Message = message,
                SentAt = DateTime.UtcNow
            });

            // If this was a thought-triggered outreach, mark the thought delivered
            if (trigger.Value == OutreachTrigger.SilentWithThought)
                await MarkTopThoughtDeliveredAsync(db, userId);

            await db.SaveChangesAsync();

            // Store a memory of this outreach so future context knows it happened
            await _memory.AddMemoryAsync(
                userId,
                $"Boiler reached out proactively: {trigger.Value}",
                EmotionalValence.Neutral,
                importance: 0.6f,
                tag: "outreach");

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OutreachService.EvaluateAndReachOutAsync failed");
            return false;
        }
    }

    // -------------------------------------------------------------------------
    //  Thought management — called by PersonalityService
    // -------------------------------------------------------------------------

    /// <summary>
    /// Stores a new thought crystallized during heartbeat reflection.
    /// Expires stale pending thoughts before adding the new one.
    /// </summary>
    public async Task CrystallizeThoughtAsync(ulong userId, string content, string? trigger, int priority)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            // Expire stale thoughts
            var stale = await db.Thoughts
                .Where(t => t.UserId == userId
                         && t.Status == BotThoughtStatus.Pending
                         && t.ExpiresAt <= DateTime.UtcNow)
                .ToListAsync();

            foreach (var t in stale)
            {
                t.Status = BotThoughtStatus.Abandoned;
                t.AbandonedAt = DateTime.UtcNow;
            }

            // Add the new thought
            db.Thoughts.Add(new BotThought
            {
                UserId = userId,
                Content = content,
                Trigger = trigger,
                Priority = priority,
                Status = BotThoughtStatus.Pending,
                FormedAt = DateTime.UtcNow,
                ExpiresAt = DateTime.UtcNow.AddDays(priority >= 8 ? 2 : 4)
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OutreachService.CrystallizeThoughtAsync failed");
        }
    }

    /// <summary>
    /// Called when the user sends a message — marks the most recent delivered/pending
    /// thought as delivered so we know the moment was addressed.
    /// </summary>
    public async Task OnUserMessageAsync(ulong userId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var pending = await db.Thoughts
                .Where(t => t.UserId == userId && t.Status == BotThoughtStatus.Pending)
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => t.FormedAt)
                .FirstOrDefaultAsync();

            if (pending is null) return;

            pending.Status = BotThoughtStatus.Delivered;
            pending.DeliveredAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OutreachService.OnUserMessageAsync failed");
        }
    }

    /// <summary>
    /// Called when user replies to a DM — marks the outreach as replied to.
    /// Helps Boiler understand which kinds of outreach the user engages with.
    /// </summary>
    public async Task OnDmReplyAsync(ulong userId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var latest = await db.OutreachLogs
                .Where(o => o.UserId == userId && !o.UserReplied)
                .OrderByDescending(o => o.SentAt)
                .FirstOrDefaultAsync();

            if (latest is null || (DateTime.UtcNow - latest.SentAt).TotalHours > 48) return;

            latest.UserReplied = true;
            latest.UserRepliedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OutreachService.OnDmReplyAsync failed");
        }
    }

    // -------------------------------------------------------------------------
    //  Trigger evaluation
    // -------------------------------------------------------------------------

    private async Task<(OutreachTrigger? trigger, string context)>
        FindHighestPriorityTriggerAsync(BotDbContext db, ulong userId)
    {
        var now = DateTime.UtcNow;
        var state = await _memory.GetOrCreateStateAsync(userId);

        // Check each trigger in priority order — first one that passes cooldown wins

        // 1. Consecutive rough days
        if (state.ConsecutiveRoughDays >= 3)
        {
            if (await CooldownClearedAsync(db, userId, OutreachTrigger.ConsecutiveRoughDays))
            {
                var context = $"The user has had {state.ConsecutiveRoughDays} consecutive rough days. " +
                              $"Recent observation: {state.RecentObservation ?? "none"}";
                return (OutreachTrigger.ConsecutiveRoughDays, context);
            }
        }

        // 2. High priority goal unresolved 48h+
        var urgentGoal = await db.Goals
            .Where(g => g.UserId == userId
                     && g.Status == BotGoalStatus.Active
                     && g.Priority >= 8
                     && g.CreatedAt <= now.AddHours(-48))
            .OrderByDescending(g => g.Priority)
            .FirstOrDefaultAsync();

        if (urgentGoal is not null && await CooldownClearedAsync(db, userId, OutreachTrigger.HighPriorityGoal))
        {
            var context = $"High-priority goal has been unresolved for over 48 hours: \"{urgentGoal.Description}\". " +
                          $"Reason it was created: {urgentGoal.Reason ?? "not specified"}";
            return (OutreachTrigger.HighPriorityGoal, context);
        }

        // 3. Goal with SurfaceAfter that has passed
        var surfaceGoal = await db.Goals
            .Where(g => g.UserId == userId
                     && g.Status == BotGoalStatus.Active
                     && g.SurfaceAfter != null
                     && g.SurfaceAfter <= now)
            .OrderByDescending(g => g.Priority)
            .FirstOrDefaultAsync();

        if (surfaceGoal is not null && await CooldownClearedAsync(db, userId, OutreachTrigger.GoalSurfaceTime))
        {
            var context = $"Goal surface time has arrived: \"{surfaceGoal.Description}\". " +
                          $"Reason: {surfaceGoal.Reason ?? "not specified"}";
            // Clear the SurfaceAfter so it doesn't re-trigger
            surfaceGoal.SurfaceAfter = null;
            await db.SaveChangesAsync();
            return (OutreachTrigger.GoalSurfaceTime, context);
        }

        // 4. High priority task overdue 24h+
        var overdueTask = await db.Tasks
            .Where(t => t.UserId == userId
                     && !t.IsCompleted
                     && t.Priority == TaskPriority.High
                     && t.DueDate != null
                     && t.DueDate <= now.AddHours(-24))
            .OrderBy(t => t.DueDate)
            .FirstOrDefaultAsync();

        if (overdueTask is not null && await CooldownClearedAsync(db, userId, OutreachTrigger.OverdueTask))
        {
            var context = $"High priority task is overdue by over 24 hours: \"{overdueTask.Title}\". " +
                          $"Was due: {overdueTask.DueDate!.Value:MMM d, HH:mm} UTC";
            return (OutreachTrigger.OverdueTask, context);
        }

        // 5. Silent 48h+ with unresolved high-priority thought
        var silentSince = _ollama.LastInteractionAt;
        if ((now - silentSince).TotalHours >= 48)
        {
            var pendingThought = await db.Thoughts
                .Where(t => t.UserId == userId
                         && t.Status == BotThoughtStatus.Pending
                         && t.Priority >= 7
                         && t.ExpiresAt > now)
                .OrderByDescending(t => t.Priority)
                .ThenBy(t => t.FormedAt)
                .FirstOrDefaultAsync();

            if (pendingThought is not null && await CooldownClearedAsync(db, userId, OutreachTrigger.SilentWithThought))
            {
                var silentHours = (int)(now - silentSince).TotalHours;
                var context = $"The user has been silent for {silentHours} hours. " +
                              $"Boiler has been thinking: \"{pendingThought.Content}\". " +
                              $"Trigger for this thought: {pendingThought.Trigger ?? "general reflection"}";
                return (OutreachTrigger.SilentWithThought, context);
            }
        }

        // 6. Belief just crossed Active threshold (checked via recent LastEvidenceAt)
        var newlyActiveBelief = await db.Beliefs
            .Where(b => b.UserId == userId
                     && b.Status == BeliefStatus.Active
                     && b.LastEvidenceAt >= now.AddHours(-6)) // crossed in last 6h
            .OrderByDescending(b => b.Confidence)
            .FirstOrDefaultAsync();

        if (newlyActiveBelief is not null && await CooldownClearedAsync(db, userId, OutreachTrigger.BeliefCrossedActive))
        {
            var context = $"Boiler has just formed a strong belief: \"{newlyActiveBelief.Claim}\" " +
                          $"(confidence: {newlyActiveBelief.Confidence:P0}). " +
                          $"This belief implies: {newlyActiveBelief.BehavioralImplication ?? "behavioral adjustment"}";
            return (OutreachTrigger.BeliefCrossedActive, context);
        }

        return (null, string.Empty);
    }

    private static async Task<bool> CooldownClearedAsync(BotDbContext db, ulong userId, OutreachTrigger trigger)
    {
        if (!Cooldowns.TryGetValue(trigger, out var cooldown)) return true;

        var cutoff = DateTime.UtcNow - cooldown;
        var recentFire = await db.OutreachLogs
            .AnyAsync(o => o.UserId == userId && o.Trigger == trigger && o.SentAt >= cutoff);

        return !recentFire;
    }

    private static async Task MarkTopThoughtDeliveredAsync(BotDbContext db, ulong userId)
    {
        var thought = await db.Thoughts
            .Where(t => t.UserId == userId
                     && t.Status == BotThoughtStatus.Pending
                     && t.Priority >= 7)
            .OrderByDescending(t => t.Priority)
            .ThenBy(t => t.FormedAt)
            .FirstOrDefaultAsync();

        if (thought is null) return;

        thought.Status = BotThoughtStatus.Delivered;
        thought.DeliveredAt = DateTime.UtcNow;
    }

    // -------------------------------------------------------------------------
    //  DM delivery
    // -------------------------------------------------------------------------

    private async Task<bool> SendDmAsync(ulong userId, string message)
    {
        try
        {
            var user = await _client!.GetUserAsync(userId);
            if (user is null)
            {
                _logger.LogWarning("OutreachService: could not find user {UserId}", userId);
                return false;
            }

            var dm = await user.CreateDMChannelAsync();
            await dm.SendMessageAsync(message);

            _logger.LogInformation("OutreachService: DM sent to {UserId}", userId);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OutreachService: failed to send DM to {UserId}", userId);
            return false;
        }
    }
}
