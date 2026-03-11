using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProductivityBot.Data;
using ProductivityBot.Models;

namespace ProductivityBot.Services;

/// <summary>
/// Manages Boiler's worldview — the beliefs it forms about the user through
/// pattern recognition and holds with tracked confidence.
///
/// This is the autonomy layer. High-confidence beliefs gate what Boiler will
/// suggest, how it responds, and what it chooses to say in rambles. Boiler
/// doesn't follow rules you wrote — it follows conclusions it reached.
///
/// Confidence thresholds:
///   Below 0.35  → Forming  — observed but not yet trusted
///   0.35–0.65   → Tentative — Boiler is aware, tone shifts slightly
///   Above 0.65  → Active   — belief is behaviorally influential
///   Below 0.15  → Retired  — too many contradictions, belief collapsed
///
/// Confidence deltas per observation:
///   Confirmation  → +0.08 (capped at 0.95)
///   Contradiction → -0.12 (harder to build than to erode)
/// </summary>
public class BeliefService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<BeliefService> _logger;

    // Thresholds
    public const float ActiveThreshold    = 0.65f;
    public const float TentativeThreshold = 0.35f;
    public const float RetirementThreshold = 0.15f;

    // Confidence deltas
    private const float ConfirmationDelta  = 0.08f;
    private const float ContradictionDelta = 0.12f;

    // How many active beliefs to inject into the system prompt
    private const int MaxBeliefsInPrompt = 6;

    public BeliefService(IServiceProvider services, ILogger<BeliefService> logger)
    {
        _services = services;
        _logger = logger;
    }

    // -------------------------------------------------------------------------
    //  Formation — called after conversation extraction and EOD passes
    // -------------------------------------------------------------------------

    /// <summary>
    /// Given a set of recent memories and facts, asks the LLM to infer new beliefs
    /// or find evidence for/against existing ones, then persists the results.
    /// </summary>
    public async Task RunBeliefInferenceAsync(ulong userId, OllamaService ollama, string evidenceContext)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var existingBeliefs = await db.Beliefs
                .Where(b => b.UserId == userId && b.Status != BeliefStatus.Retired)
                .ToListAsync();

            var prompt = BuildInferencePrompt(evidenceContext, existingBeliefs);
            var raw = await ollama.InferBeliefsAsync(prompt);

            if (string.IsNullOrWhiteSpace(raw) || raw.Trim().Equals("NOTHING", StringComparison.OrdinalIgnoreCase))
                return;

            var parsed = ParseInferenceOutput(raw);

            foreach (var (key, claim, domain, implication, isNew) in parsed.newBeliefs)
                await UpsertBeliefAsync(db, userId, key, claim, domain, implication);

            foreach (var key in parsed.confirmations)
                await AdjustConfidenceAsync(db, userId, key, confirming: true);

            foreach (var key in parsed.contradictions)
                await AdjustConfidenceAsync(db, userId, key, confirming: false);

            await db.SaveChangesAsync();

            _logger.LogDebug("BeliefService: inference complete for {UserId}. New: {New}, Confirmed: {Confirmed}, Contradicted: {Contradicted}",
                userId, parsed.newBeliefs.Count, parsed.confirmations.Count, parsed.contradictions.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BeliefService.RunBeliefInferenceAsync failed");
        }
    }

    // -------------------------------------------------------------------------
    //  Behavioral gate — called before prompt generation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Returns a block of behavioral directives derived from active high-confidence beliefs.
    /// This is injected into Boiler's system prompt so beliefs gate behavior automatically.
    /// Returns empty string if no active beliefs yet.
    /// </summary>
    public async Task<string> GetBehavioralDirectivesAsync(ulong userId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var activeBeliefs = await db.Beliefs
                .Where(b => b.UserId == userId
                         && b.Status == BeliefStatus.Active
                         && b.Confidence >= ActiveThreshold
                         && !string.IsNullOrEmpty(b.BehavioralImplication))
                .OrderByDescending(b => b.Confidence)
                .Take(MaxBeliefsInPrompt)
                .ToListAsync();

            if (!activeBeliefs.Any()) return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== YOUR WORLDVIEW — BEHAVIORAL GUIDELINES ===");
            sb.AppendLine("You have formed these beliefs about your owner through observation.");
            sb.AppendLine("They are yours. Act on them.");
            sb.AppendLine();

            foreach (var belief in activeBeliefs)
            {
                var strength = belief.Confidence >= 0.85f ? "strongly believe"
                             : belief.Confidence >= 0.65f ? "believe"
                             : "tentatively believe";
                sb.AppendLine($"You {strength}: {belief.Claim}");
                sb.AppendLine($"  → {belief.BehavioralImplication}");
            }

            sb.AppendLine("=== END WORLDVIEW ===");
            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BeliefService.GetBehavioralDirectivesAsync failed");
            return string.Empty;
        }
    }

    /// <summary>
    /// Returns tentative beliefs (confidence between thresholds) as awareness hints —
    /// softer than directives, just shapes tone.
    /// </summary>
    public async Task<string> GetTentativeAwarenessAsync(ulong userId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var tentative = await db.Beliefs
                .Where(b => b.UserId == userId
                         && b.Confidence >= TentativeThreshold
                         && b.Confidence < ActiveThreshold
                         && b.Status != BeliefStatus.Retired)
                .OrderByDescending(b => b.Confidence)
                .Take(3)
                .ToListAsync();

            if (!tentative.Any()) return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("You're not sure yet, but you're starting to notice:");
            foreach (var b in tentative)
                sb.AppendLine($"  - {b.Claim} (still forming your view on this)");

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BeliefService.GetTentativeAwarenessAsync failed");
            return string.Empty;
        }
    }

    // -------------------------------------------------------------------------
    //  Introspection — for !boiler beliefs and !memory recall
    // -------------------------------------------------------------------------

    public async Task<string> FormatBeliefsForDisplayAsync(ulong userId)
    {
        try
        {
            using var scope = _services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

            var beliefs = await db.Beliefs
                .Where(b => b.UserId == userId && b.Status != BeliefStatus.Retired)
                .OrderByDescending(b => b.Confidence)
                .ToListAsync();

            if (!beliefs.Any())
                return "I haven't formed any strong beliefs about you yet — give it time.";

            var sb = new System.Text.StringBuilder();

            var active = beliefs.Where(b => b.Status == BeliefStatus.Active).ToList();
            var forming = beliefs.Where(b => b.Status == BeliefStatus.Forming).ToList();

            if (active.Any())
            {
                sb.AppendLine("**What I believe about you:**");
                foreach (var b in active)
                {
                    var bar = ConfidenceBar(b.Confidence);
                    sb.AppendLine($"{bar} {b.Claim}");
                    sb.AppendLine($"  *{b.ConfirmationCount} confirmations, {b.ContradictionCount} contradictions*");
                }
                sb.AppendLine();
            }

            if (forming.Any())
            {
                sb.AppendLine("**Still forming opinions on:**");
                foreach (var b in forming)
                {
                    var bar = ConfidenceBar(b.Confidence);
                    sb.AppendLine($"{bar} {b.Claim}");
                }
            }

            return sb.ToString().Trim();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BeliefService.FormatBeliefsForDisplayAsync failed");
            return "Something went wrong reading beliefs.";
        }
    }

    public async Task<List<BotBelief>> GetActiveBeliefsAsync(ulong userId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        return await db.Beliefs
            .Where(b => b.UserId == userId && b.Status == BeliefStatus.Active)
            .OrderByDescending(b => b.Confidence)
            .ToListAsync();
    }

    // -------------------------------------------------------------------------
    //  Private helpers
    // -------------------------------------------------------------------------

    private static async Task UpsertBeliefAsync(BotDbContext db, ulong userId,
        string key, string claim, BeliefDomain domain, string? implication)
    {
        var existing = await db.Beliefs
            .FirstOrDefaultAsync(b => b.UserId == userId && b.BeliefKey == key);

        if (existing is not null)
        {
            // Update claim text if it's evolved — keep the confidence and counts
            existing.Claim = claim;
            if (!string.IsNullOrWhiteSpace(implication))
                existing.BehavioralImplication = implication;
            existing.LastEvidenceAt = DateTime.UtcNow;
            return;
        }

        db.Beliefs.Add(new BotBelief
        {
            UserId = userId,
            Claim = claim,
            BeliefKey = key,
            Domain = domain,
            BehavioralImplication = implication,
            Confidence = 0.4f, // starts in forming range
            Status = BeliefStatus.Forming,
            FormationEvidence = claim,
            FormedAt = DateTime.UtcNow,
            LastEvidenceAt = DateTime.UtcNow
        });
    }

    private static async Task AdjustConfidenceAsync(BotDbContext db, ulong userId,
        string key, bool confirming)
    {
        var belief = await db.Beliefs
            .FirstOrDefaultAsync(b => b.UserId == userId && b.BeliefKey == key);

        if (belief is null) return;

        if (confirming)
        {
            belief.Confidence = Math.Min(0.95f, belief.Confidence + ConfirmationDelta);
            belief.ConfirmationCount++;
        }
        else
        {
            belief.Confidence = Math.Max(0f, belief.Confidence - ContradictionDelta);
            belief.ContradictionCount++;
        }

        belief.LastEvidenceAt = DateTime.UtcNow;

        // Update status based on new confidence
        belief.Status = belief.Confidence switch
        {
            >= ActiveThreshold    => BeliefStatus.Active,
            <= RetirementThreshold => BeliefStatus.Retired,
            _                     => BeliefStatus.Forming
        };

        if (belief.Status == BeliefStatus.Retired && belief.RetiredAt is null)
            belief.RetiredAt = DateTime.UtcNow;
    }

    private static string BuildInferencePrompt(string evidenceContext, List<BotBelief> existingBeliefs)
    {
        var existingSection = existingBeliefs.Any()
            ? "EXISTING BELIEFS (provide BeliefKey to confirm or contradict these):\n" +
              string.Join("\n", existingBeliefs.Select(b =>
                  $"  key={b.BeliefKey} | claim={b.Claim} | confidence={b.Confidence:F2}"))
            : "EXISTING BELIEFS: none yet";

        return $"""
            You are Boiler's belief inference system.
            Your job is to analyse recent evidence about the owner and determine:
              1. Whether any new beliefs should be formed
              2. Whether existing beliefs are confirmed or contradicted

            {existingSection}

            RECENT EVIDENCE:
            {evidenceContext}

            Output format (one entry per line, no commentary):

            For new beliefs:
            BELIEF|<belief_key>|<claim>|<domain>|<behavioral_implication>
              belief_key: snake_case identifier, e.g. "overcommits_when_excited"
              claim: plain English, e.g. "tends to overcommit when starting new projects"
              domain: behavior | emotional | productivity | health | relationships | values
              behavioral_implication: what Boiler should DO differently because of this belief

            For existing belief updates:
            CONFIRM|<belief_key>
            CONTRADICT|<belief_key>

            Rules:
            - Only output beliefs that are genuinely pattern-based. One data point is not a belief.
            - Behavioral implications must be actionable directives, not observations.
            - If nothing warrants a belief update, output: NOTHING
            - Max 2 new beliefs per inference pass.
            - Be specific. "user is sometimes stressed" is useless. "user's productivity collapses when university deadlines overlap" is a belief.
            """;
    }

    private static (List<(string key, string claim, BeliefDomain domain, string? implication, bool isNew)> newBeliefs,
                    List<string> confirmations,
                    List<string> contradictions)
        ParseInferenceOutput(string raw)
    {
        var newBeliefs = new List<(string, string, BeliefDomain, string?, bool)>();
        var confirmations = new List<string>();
        var contradictions = new List<string>();

        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.Equals("NOTHING", StringComparison.OrdinalIgnoreCase)) break;

            var parts = line.Split('|');
            if (parts.Length < 2) continue;

            switch (parts[0].Trim().ToUpper())
            {
                case "BELIEF" when parts.Length >= 5:
                    var key = parts[1].Trim().ToLower().Replace(' ', '_');
                    var claim = parts[2].Trim();
                    var domain = ParseDomain(parts[3].Trim());
                    var implication = parts[4].Trim();
                    if (!string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(claim))
                        newBeliefs.Add((key, claim, domain, implication, true));
                    break;

                case "CONFIRM" when parts.Length >= 2:
                    var confirmKey = parts[1].Trim().ToLower();
                    if (!string.IsNullOrWhiteSpace(confirmKey))
                        confirmations.Add(confirmKey);
                    break;

                case "CONTRADICT" when parts.Length >= 2:
                    var contradictKey = parts[1].Trim().ToLower();
                    if (!string.IsNullOrWhiteSpace(contradictKey))
                        contradictions.Add(contradictKey);
                    break;
            }
        }

        return (newBeliefs, confirmations, contradictions);
    }

    private static BeliefDomain ParseDomain(string s) => s.ToLower() switch
    {
        "emotional"     => BeliefDomain.Emotional,
        "productivity"  => BeliefDomain.Productivity,
        "health"        => BeliefDomain.Health,
        "relationships" => BeliefDomain.Relationships,
        "values"        => BeliefDomain.Values,
        _               => BeliefDomain.Behavior
    };

    private static string ConfidenceBar(float confidence)
    {
        // Simple visual indicator
        return confidence switch
        {
            >= 0.85f => "🟦🟦🟦🟦🟦",
            >= 0.70f => "🟦🟦🟦🟦⬜",
            >= 0.55f => "🟦🟦🟦⬜⬜",
            >= 0.40f => "🟦🟦⬜⬜⬜",
            _        => "🟦⬜⬜⬜⬜"
        };
    }
}
