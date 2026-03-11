using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProductivityBot.Data;
using ProductivityBot.Models;

namespace ProductivityBot.Services;

/// <summary>
/// The brain of the companion memory system.
///
/// Responsibilities:
///   1. Hydrate  — before an !ask prompt, pull relevant memories + facts and format
///                 them for injection into the system prompt.
///   2. Extract  — after an !ask exchange, ask the LLM to extract any new facts or
///                 memorable moments from the conversation and persist them.
///   3. Prune    — periodically decay and remove low-importance old memories.
/// </summary>
public class MemoryService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<MemoryService> _logger;

    // How many memories / facts to surface per prompt — enough context without blowing token budget
    private const int MaxMemoriesInPrompt = 8;
    private const int MaxFactsInPrompt = 12;

    // Importance threshold below which memories become candidates for pruning
    private const float PruneThreshold = 0.2f;

    // Memories older than this with low importance get pruned
    private static readonly TimeSpan PruneAge = TimeSpan.FromDays(30);

    public MemoryService(IServiceProvider services, ILogger<MemoryService> logger)
    {
        _services = services;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    //  Hydration — called before each !ask prompt
    // -------------------------------------------------------------------------

    /// <summary>
    /// Builds a memory context block to inject into the system prompt.
    /// Returns an empty string if there's nothing relevant yet.
    /// </summary>
    public async Task<string> HydrateAsync(ulong userId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var facts = await GetRelevantFactsAsync(db, userId);
            var memories = await GetRecentMemoriesAsync(db, userId);
            var state = await GetOrCreateStateAsync(db, userId);
            var goals = await GetActiveGoalsAsync(db, userId);

            if (!facts.Any() && !memories.Any() && goals.Count == 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== WHAT YOU KNOW ABOUT YOUR OWNER ===");
            sb.AppendLine("(Use this naturally in conversation — don't recite it, just let it inform how you respond.)");
            sb.AppendLine();

            if (facts.Any())
            {
                sb.AppendLine("Facts you know:");
                foreach (var fact in facts)
                    sb.AppendLine($"  - {fact.Content}");
                sb.AppendLine();
            }

            if (memories.Any())
            {
                sb.AppendLine("Recent things you remember:");
                foreach (var mem in memories)
                {
                    var when = FormatRelativeTime(mem.OccurredAt);
                    sb.AppendLine($"  - [{when}] {mem.Content}");
                }
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(state.CurrentThought))
            {
                sb.AppendLine($"You've been thinking: {state.CurrentThought}");
                sb.AppendLine();
            }

            if (goals.Any())
            {
                sb.AppendLine("Things you're keeping an eye on:");
                foreach (var goal in goals)
                    sb.AppendLine($"  - {goal.Description}");
                sb.AppendLine();
            }

            sb.AppendLine("=== END OF CONTEXT ===");

            // Bump reference counts for surfaced memories
            await BumpReferencesAsync(db, memories.Select(m => m.Id).ToList());

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryService.HydrateAsync failed — continuing without memory context");
            return string.Empty;
        }
    }

    // -------------------------------------------------------------------------
    //  Extraction — called after each !ask exchange
    // -------------------------------------------------------------------------

    /// <summary>
    /// Given the full conversation exchange (user prompt + Boiler's reply),
    /// asks the LLM to extract memorable facts and moments, then persists them.
    ///
    /// This is intentionally async fire-and-forget from the caller's perspective
    /// so it doesn't slow down the !ask response.
    /// </summary>
    public async Task ExtractAndStoreAsync(ulong userId, string userMessage, string boilerReply, OllamaService ollama)
    {
        try
        {
            var extractionPrompt = BuildExtractionPrompt(userMessage, boilerReply);
            var raw = await ollama.ExtractMemoryAsync(extractionPrompt);

            if (string.IsNullOrWhiteSpace(raw)) return;

            var parsed = ParseExtractionOutput(raw);
            if (parsed.facts.Count == 0 && parsed.memories.Count == 0) return;

            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            foreach (var (content, category, key, confidence) in parsed.facts)
                await UpsertFactAsync(db, userId, content, category, key, confidence, userProvided: true);

            foreach (var (content, valence, importance, tag) in parsed.memories)
                await AddMemoryAsync(db, userId, content, valence, importance, tag);

            await db.SaveChangesAsync();

            _logger.LogDebug("MemoryService: stored {Facts} facts and {Memories} memories for user {UserId}",
                parsed.facts.Count, parsed.memories.Count, userId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryService.ExtractAndStoreAsync failed");
        }
    }

    // -------------------------------------------------------------------------
    //  Direct storage — called by EodService and PersonalityService
    // -------------------------------------------------------------------------

    public async Task AddMemoryAsync(ulong userId, string content, EmotionalValence valence = EmotionalValence.Neutral,
        float importance = 0.5f, string? tag = null)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            await AddMemoryAsync(db, userId, content, valence, importance, tag);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryService.AddMemoryAsync failed");
        }
    }

    public async Task UpsertFactAsync(ulong userId, string content, FactCategory category = FactCategory.General,
        string? factKey = null, float confidence = 0.8f, bool userProvided = false)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            await UpsertFactAsync(db, userId, content, category, factKey, confidence, userProvided);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryService.UpsertFactAsync failed");
        }
    }

    public async Task AddGoalAsync(ulong userId, string description, string? reason = null,
        int priority = 5, DateTime? surfaceAfter = null)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            db.Goals.Add(new BotGoal
            {
                UserId = userId,
                Description = description,
                Reason = reason,
                Priority = priority,
                SurfaceAfter = surfaceAfter
            });

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryService.AddGoalAsync failed");
        }
    }

    public async Task ResolveGoalAsync(ulong userId, int goalId, string? notes = null)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == goalId && g.UserId == userId);
            if (goal is null) return;

            goal.Status = BotGoalStatus.Resolved;
            goal.ResolvedAt = DateTime.UtcNow;
            if (notes is not null) goal.Notes = notes;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryService.ResolveGoalAsync failed");
        }
    }

    // -------------------------------------------------------------------------
    //  State access
    // -------------------------------------------------------------------------

    public async Task<BotState> GetOrCreateStateAsync(ulong userId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        return await GetOrCreateStateAsync(db, userId);
    }

    public async Task UpdateStateAsync(ulong userId, Action<BotState> mutate)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            var state = await GetOrCreateStateAsync(db, userId);
            mutate(state);
            state.LastUpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryService.UpdateStateAsync failed");
        }
    }

    // -------------------------------------------------------------------------
    //  Memory recall — for !memory recall command
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a human-readable summary of what Boiler remembers about a user.
    /// Optionally filtered by a keyword query (simple substring match — no vector search needed).
    /// </summary>
    public async Task<string> RecallAsync(ulong userId, string? query = null)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var factsQuery = db.Facts.Where(f => f.UserId == userId);
            var memoriesQuery = db.Memories.Where(m => m.UserId == userId);

            if (!string.IsNullOrWhiteSpace(query))
            {
                var q = query.ToLower();
                factsQuery = factsQuery.Where(f => f.Content.ToLower().Contains(q));
                memoriesQuery = memoriesQuery.Where(m => m.Content.ToLower().Contains(q));
            }

            var facts = await factsQuery
                .OrderByDescending(f => f.Confidence)
                .Take(15)
                .ToListAsync();

            var memories = await memoriesQuery
                .OrderByDescending(m => m.Importance)
                .ThenByDescending(m => m.OccurredAt)
                .Take(10)
                .ToListAsync();

            var goals = await db.Goals
                .Where(g => g.UserId == userId && g.Status == BotGoalStatus.Active)
                .OrderByDescending(g => g.Priority)
                .Take(5)
                .ToListAsync();

            var state = await GetOrCreateStateAsync(db, userId);

            if (!facts.Any() && !memories.Any() && !goals.Any())
                return string.IsNullOrWhiteSpace(query)
                    ? "I don't have much stored yet — we haven't talked enough for me to build a picture of you."
                    : $"I don't remember anything matching \"{query}\" yet.";

            var sb = new System.Text.StringBuilder();

            sb.AppendLine($"**Current mood:** {state.CurrentMood}");
            if (!string.IsNullOrWhiteSpace(state.CurrentThought))
                sb.AppendLine($"**On my mind:** {state.CurrentThought}");
            sb.AppendLine();

            if (facts.Any())
            {
                sb.AppendLine("**What I know about you:**");
                foreach (var f in facts)
                {
                    var confidence = f.Confidence >= 0.9f ? "" : f.Confidence >= 0.6f ? " *(inferred)*" : " *(guessing)*";
                    sb.AppendLine($"• {f.Content}{confidence}");
                }
                sb.AppendLine();
            }

            if (memories.Any())
            {
                sb.AppendLine("**Things I remember:**");
                foreach (var m in memories)
                {
                    var when = FormatRelativeTime(m.OccurredAt);
                    var valenceIcon = m.Valence switch
                    {
                        EmotionalValence.Positive => "🟢",
                        EmotionalValence.Negative => "🔴",
                        _ => "⚪"
                    };
                    sb.AppendLine($"{valenceIcon} [{when}] {m.Content}");
                }
                sb.AppendLine();
            }

            if (goals.Any())
            {
                sb.AppendLine("**Things I'm keeping an eye on:**");
                foreach (var g in goals)
                    sb.AppendLine($"• {g.Description}");
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryService.RecallAsync failed");
            return "Something went wrong trying to recall memories.";
        }
    }

    // -------------------------------------------------------------------------
    //  Maintenance — called periodically by PersonalityService heartbeat
    // -------------------------------------------------------------------------

    /// <summary>
    /// Decays old low-importance memories and prunes the ones that have faded completely.
    /// Safe to call daily.
    /// </summary>
    public async Task RunMaintenanceAsync(ulong userId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var cutoff = DateTime.UtcNow - PruneAge;

            // Decay: reduce importance of old, unreferenced memories
            var toDecay = await db.Memories
                .Where(m => m.UserId == userId
                         && m.OccurredAt < cutoff
                         && m.TimesReferenced < 3
                         && m.Importance > PruneThreshold)
                .ToListAsync();

            foreach (var mem in toDecay)
                mem.Importance = Math.Max(0, mem.Importance - 0.1f);

            // Prune: remove memories that have decayed below the threshold and haven't been referenced
            var toPrune = await db.Memories
                .Where(m => m.UserId == userId
                         && m.OccurredAt < cutoff
                         && m.Importance <= PruneThreshold
                         && m.TimesReferenced == 0)
                .ToListAsync();

            if (toPrune.Any())
            {
                db.Memories.RemoveRange(toPrune);
                _logger.LogInformation("MemoryService: pruned {Count} low-importance memories for user {UserId}", toPrune.Count, userId);
            }

            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MemoryService.RunMaintenanceAsync failed");
        }
    }

    // -------------------------------------------------------------------------
    //  Private helpers
    // -------------------------------------------------------------------------

    private static async Task<List<BotFact>> GetRelevantFactsAsync(BotDbContext db, ulong userId)
        => await db.Facts
            .Where(f => f.UserId == userId && f.Confidence >= 0.5f)
            .OrderByDescending(f => f.Confidence)
            .ThenByDescending(f => f.UpdatedAt)
            .Take(MaxFactsInPrompt)
            .ToListAsync();

    private static async Task<List<BotMemory>> GetRecentMemoriesAsync(BotDbContext db, ulong userId)
        => await db.Memories
            .Where(m => m.UserId == userId)
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.OccurredAt)
            .Take(MaxMemoriesInPrompt)
            .ToListAsync();

    private static async Task<List<BotGoal>> GetActiveGoalsAsync(BotDbContext db, ulong userId)
        => await db.Goals
            .Where(g => g.UserId == userId
                     && g.Status == BotGoalStatus.Active
                     && (g.SurfaceAfter == null || g.SurfaceAfter <= DateTime.UtcNow))
            .OrderByDescending(g => g.Priority)
            .Take(3)
            .ToListAsync();

    private static async Task<BotState> GetOrCreateStateAsync(BotDbContext db, ulong userId)
    {
        var state = await db.BotStates.FirstOrDefaultAsync(s => s.UserId == userId);
        if (state is not null) return state;

        state = new BotState { UserId = userId };
        db.BotStates.Add(state);
        await db.SaveChangesAsync();
        return state;
    }

    private static async Task AddMemoryAsync(BotDbContext db, ulong userId, string content,
        EmotionalValence valence, float importance, string? tag)
    {
        db.Memories.Add(new BotMemory
        {
            UserId = userId,
            Content = content,
            Valence = valence,
            Importance = importance,
            Tag = tag,
            OccurredAt = DateTime.UtcNow
        });
    }

    private static async Task UpsertFactAsync(BotDbContext db, ulong userId, string content,
        FactCategory category, string? factKey, float confidence, bool userProvided)
    {
        // If a factKey is provided, try to update the existing fact instead of adding a duplicate
        if (!string.IsNullOrWhiteSpace(factKey))
        {
            var existing = await db.Facts.FirstOrDefaultAsync(
                f => f.UserId == userId && f.FactKey == factKey);

            if (existing is not null)
            {
                existing.Content = content;
                existing.Confidence = confidence;
                existing.UpdatedAt = DateTime.UtcNow;
                existing.UserProvided = userProvided || existing.UserProvided;
                return;
            }
        }

        db.Facts.Add(new BotFact
        {
            UserId = userId,
            Content = content,
            Category = category,
            FactKey = factKey,
            Confidence = confidence,
            UserProvided = userProvided
        });
    }

    private static async Task BumpReferencesAsync(BotDbContext db, List<int> memoryIds)
    {
        if (!memoryIds.Any()) return;
        var memories = await db.Memories.Where(m => memoryIds.Contains(m.Id)).ToListAsync();
        foreach (var m in memories)
        {
            m.TimesReferenced++;
            m.LastReferencedAt = DateTime.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    // -------------------------------------------------------------------------
    //  Extraction prompt + parser
    // -------------------------------------------------------------------------

    private static string BuildExtractionPrompt(string userMessage, string boilerReply)
        => $"""
            You are a memory extraction system. Analyse the following conversation exchange and extract:

            1. FACTS: Stable facts about the user that Boiler should remember long-term.
               Format each as: FACT|<content>|<category>|<key>|<confidence>
               Categories: general, personal, work, health, patterns, preferences, goals
               Key: a short snake_case identifier for this fact (e.g. "user_location", "user_job") — leave blank if not applicable
               Confidence: 0.5–1.0

            2. MEMORIES: Noteworthy moments or emotional signals worth remembering.
               Format each as: MEMORY|<content>|<valence>|<importance>|<tag>
               Valence: positive, negative, neutral
               Importance: 0.1–1.0 (1.0 = very significant, 0.1 = minor)
               Tag: conversation, task, habit, health, project, or leave blank

            Rules:
            - Only extract things genuinely worth remembering. Do NOT extract filler or pleasantries.
            - If nothing is worth extracting, output: NOTHING
            - One item per line. No extra commentary.

            USER: {userMessage}
            BOILER: {boilerReply}
            """;

    private static (List<(string content, FactCategory category, string? key, float confidence)> facts,
                    List<(string content, EmotionalValence valence, float importance, string? tag)> memories)
        ParseExtractionOutput(string raw)
    {
        var facts = new List<(string, FactCategory, string?, float)>();
        var memories = new List<(string, EmotionalValence, float, string?)>();

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Equals("NOTHING", StringComparison.OrdinalIgnoreCase)) break;

            var parts = line.Split('|');

            if (parts.Length >= 5 && parts[0].Equals("FACT", StringComparison.OrdinalIgnoreCase))
            {
                var content = parts[1].Trim();
                var category = ParseFactCategory(parts[2].Trim());
                var key = string.IsNullOrWhiteSpace(parts[3]) ? null : parts[3].Trim();
                float.TryParse(parts[4].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var confidence);
                confidence = Math.Clamp(confidence, 0.1f, 1.0f);

                if (!string.IsNullOrWhiteSpace(content))
                    facts.Add((content, category, key, confidence));
            }
            else if (parts.Length >= 5 && parts[0].Equals("MEMORY", StringComparison.OrdinalIgnoreCase))
            {
                var content = parts[1].Trim();
                var valence = ParseValence(parts[2].Trim());
                float.TryParse(parts[3].Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var importance);
                importance = Math.Clamp(importance, 0.1f, 1.0f);
                var tag = string.IsNullOrWhiteSpace(parts[4]) ? null : parts[4].Trim();

                if (!string.IsNullOrWhiteSpace(content))
                    memories.Add((content, valence, importance, tag));
            }
        }

        return (facts, memories);
    }

    private static FactCategory ParseFactCategory(string s) => s.ToLower() switch
    {
        "personal"    => FactCategory.Personal,
        "work"        => FactCategory.Work,
        "health"      => FactCategory.Health,
        "patterns"    => FactCategory.Patterns,
        "preferences" => FactCategory.Preferences,
        "goals"       => FactCategory.Goals,
        _             => FactCategory.General
    };

    private static EmotionalValence ParseValence(string s) => s.ToLower() switch
    {
        "positive" => EmotionalValence.Positive,
        "negative" => EmotionalValence.Negative,
        _          => EmotionalValence.Neutral
    };

    private static string FormatRelativeTime(DateTime dt)
    {
        var delta = DateTime.UtcNow - dt;
        if (delta.TotalMinutes < 60)  return "just now";
        if (delta.TotalHours < 24)    return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7)      return $"{(int)delta.TotalDays}d ago";
        if (delta.TotalDays < 30)     return $"{(int)(delta.TotalDays / 7)}w ago";
        return dt.ToString("MMM d");
    }
}
