using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProductivityBot.Models;

namespace ProductivityBot.Services;

/// <summary>
/// Manages Boiler's persistent internal state — mood, current thought, beliefs about the user.
///
/// Runs a periodic reflection heartbeat that:
///   1. Reviews recent memories and EOD signals
///   2. Updates BotState (mood, current thought, streak counters)
///   3. Generates or promotes goals based on observed patterns
///   4. Runs memory maintenance (decay + pruning)
///
/// This is the layer that makes Boiler feel like it's been "thinking" between sessions.
/// </summary>
public class PersonalityService
{
    private readonly MemoryService _memory;
    private readonly OllamaService _ollama;
    private readonly IConfiguration _config;
    private readonly ILogger<PersonalityService> _logger;

    private OutreachService? _outreach;

    private Timer? _heartbeatTimer;
    private Timer? _maintenanceTimer;

    private ulong _ownerId;

    public void SetOutreachService(OutreachService outreach) => _outreach = outreach;

    // Heartbeat runs every 4 hours — updates mood/thought/goals without spamming
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(4);

    // Maintenance runs once daily
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(24);

    public PersonalityService(
        MemoryService memory,
        OllamaService ollama,
        IConfiguration config,
        ILogger<PersonalityService> logger)
    {
        _memory = memory;
        _ollama = ollama;
        _config = config;
        _logger = logger;
    }

    public void Start(DiscordSocketClient client)
    {
        if (!ulong.TryParse(_config["Discord:OwnerId"], out _ownerId) || _ownerId == 0)
        {
            _logger.LogWarning("PersonalityService: OwnerId not configured — heartbeat disabled.");
            return;
        }

        // Stagger the first heartbeat by 30 minutes after startup — let the bot settle
        _heartbeatTimer = new Timer(
            HeartbeatCallback,
            null,
            TimeSpan.FromMinutes(30),
            HeartbeatInterval);

        _maintenanceTimer = new Timer(
            MaintenanceCallback,
            null,
            TimeSpan.FromHours(1),
            MaintenanceInterval);

        _logger.LogInformation("PersonalityService started. Heartbeat every {Hours}h.", HeartbeatInterval.TotalHours);
    }

    public void Stop()
    {
        _heartbeatTimer?.Dispose();
        _maintenanceTimer?.Dispose();
    }

    // -------------------------------------------------------------------------
    //  Heartbeat — reflection pass
    // -------------------------------------------------------------------------

    private async void HeartbeatCallback(object? _)
    {
        if (!_ollama.IsEnabled) return;

        try
        {
            _logger.LogInformation("PersonalityService: running reflection heartbeat for user {UserId}", _ownerId);
            await RunReflectionAsync(_ownerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PersonalityService: heartbeat error");
        }
    }

    /// <summary>
    /// Public entry point so EodService can trigger a reflection pass after the EOD summary is built.
    /// </summary>
    public async Task RunReflectionAsync(ulong userId)
    {
        if (!_ollama.IsEnabled) return;

        var state = await _memory.GetOrCreateStateAsync(userId);
        var memoryContext = await _memory.HydrateAsync(userId);

        if (string.IsNullOrWhiteSpace(memoryContext))
        {
            _logger.LogDebug("PersonalityService: no memory context yet — skipping reflection.");
            return;
        }

        var reflectionPrompt = BuildReflectionPrompt(state, memoryContext);
        var raw = await _ollama.ReflectAsync(reflectionPrompt);

        if (string.IsNullOrWhiteSpace(raw)) return;

        var parsed = ParseReflectionOutput(raw);

        await _memory.UpdateStateAsync(userId, s =>
        {
            if (!string.IsNullOrWhiteSpace(parsed.mood))
                s.CurrentMood = parsed.mood;

            if (!string.IsNullOrWhiteSpace(parsed.thought))
                s.CurrentThought = parsed.thought;

            if (!string.IsNullOrWhiteSpace(parsed.observation))
                s.RecentObservation = parsed.observation;
        });

        // Store any new goals the reflection produced
        foreach (var (description, reason, priority) in parsed.newGoals)
            await _memory.AddGoalIfNewAsync(userId, description, _ollama, reason, priority);

        // Store the reflection itself as a memory so it feeds future reflections
        if (!string.IsNullOrWhiteSpace(parsed.observation))
            await _memory.AddMemoryAsync(
                userId,
                $"Boiler reflected: {parsed.observation}",
                EmotionalValence.Neutral,
                importance: 0.4f,
                tag: "observation");

        // Crystallize a thought if the reflection produced one worth tracking
        if (_outreach is not null && !string.IsNullOrWhiteSpace(parsed.thought))
        {
            // Priority is elevated if rough days are stacking or observation sounds urgent
            var thoughtPriority = state.ConsecutiveRoughDays >= 2 ? 7 :
                                  parsed.thought.Contains("concern", StringComparison.OrdinalIgnoreCase) ||
                                  parsed.thought.Contains("worried", StringComparison.OrdinalIgnoreCase) ||
                                  parsed.thought.Contains("miss", StringComparison.OrdinalIgnoreCase) ? 6 : 4;

            await _outreach.CrystallizeThoughtAsync(
                userId,
                parsed.thought,
                trigger: string.IsNullOrWhiteSpace(parsed.observation) ? null : parsed.observation,
                priority: thoughtPriority);
        }

        // Evaluate outreach — fire-and-forget, never blocks reflection
        if (_outreach is not null)
            _ = Task.Run(() => _outreach.EvaluateAndReachOutAsync(userId));

        _logger.LogInformation("PersonalityService: reflection complete. Mood: {Mood} | Thought: {Thought}",
            parsed.mood, parsed.thought);
    }

    // -------------------------------------------------------------------------
    //  Maintenance — memory decay + pruning
    // -------------------------------------------------------------------------

    private async void MaintenanceCallback(object? _)
    {
        try
        {
            _logger.LogInformation("PersonalityService: running memory maintenance for user {UserId}", _ownerId);
            await _memory.RunMaintenanceAsync(_ownerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PersonalityService: maintenance error");
        }
    }

    // -------------------------------------------------------------------------
    //  Reflection prompt + parser
    // -------------------------------------------------------------------------

    private static string BuildReflectionPrompt(BotState state, string memoryContext)
        => $"""
            You are Boiler's internal reflection system.
            Your job is to update Boiler's internal state based on what you know about the owner.

            CURRENT STATE:
            Mood: {state.CurrentMood}
            Currently thinking: {state.CurrentThought ?? "nothing in particular"}
            Recent observation: {state.RecentObservation ?? "none"}

            MEMORY CONTEXT:
            {memoryContext}

            Based on the above, output the following (one per line, exactly this format):
            MOOD|<short mood description, e.g. "a bit worried, but trying to stay upbeat">
            THOUGHT|<what Boiler is currently thinking about, 1 sentence>
            OBSERVATION|<a 1-2 sentence observation about the owner's recent patterns or wellbeing>
            GOAL|<description>|<reason>|<priority 1-10>

            Rules:
            - MOOD, THOUGHT, and OBSERVATION are required.
            - Only output GOAL lines if something genuinely warrants tracking. 0-2 goals max.
            - Goals should be things Boiler wants to follow up on, not things the user explicitly set.
            - Be specific. "User seems tired" is worse than "Gvidas has missed 3 habit logs this week — worth a gentle nudge".
            - No extra commentary. Just the formatted lines.
            """;

    private static (string mood, string thought, string observation,
                    List<(string description, string reason, int priority)> newGoals)
        ParseReflectionOutput(string raw)
    {
        string mood = string.Empty;
        string thought = string.Empty;
        string observation = string.Empty;
        var goals = new List<(string, string, int)>();

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split('|');
            if (parts.Length < 2) continue;

            switch (parts[0].ToUpper())
            {
                case "MOOD":
                    mood = parts[1].Trim();
                    break;
                case "THOUGHT":
                    thought = parts[1].Trim();
                    break;
                case "OBSERVATION":
                    observation = parts[1].Trim();
                    break;
                case "GOAL" when parts.Length >= 4:
                    var desc = parts[1].Trim();
                    var reason = parts[2].Trim();
                    int.TryParse(parts[3].Trim(), out var priority);
                    priority = Math.Clamp(priority, 1, 10);
                    if (!string.IsNullOrWhiteSpace(desc))
                        goals.Add((desc, reason, priority));
                    break;
            }
        }

        return (mood, thought, observation, goals);
    }
}
