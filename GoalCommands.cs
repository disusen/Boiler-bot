using Discord;
using Discord.Commands;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ProductivityBot.Data;
using ProductivityBot.Models;
using ProductivityBot.Services;

namespace ProductivityBot.Commands;

[Group("goal")]
[Alias("g")]
[Summary("Goal tracking commands")]
public class GoalCommands : ModuleBase<SocketCommandContext>
{
    private readonly MemoryService _memory;
    private readonly BeliefService _beliefs;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;

    public GoalCommands(MemoryService memory, BeliefService beliefs, IServiceProvider services, IConfiguration config)
    {
        _memory = memory;
        _beliefs = beliefs;
        _services = services;
        _config = config;
    }

    [Command("list")]
    [Alias("l", "ls")]
    [Summary("List active goals. Usage: !goal list [all]")]
    public async Task ListGoalsAsync([Remainder] string? filter = null)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var showAll = filter?.Trim().ToLower() == "all";

        var query = db.Goals.Where(g => g.UserId == Context.User.Id);
        query = showAll
            ? query.Where(g => g.Status != BotGoalStatus.Abandoned)
            : query.Where(g => g.Status == BotGoalStatus.Active);

        var goals = await query.OrderByDescending(g => g.Priority).ThenBy(g => g.CreatedAt).ToListAsync();

        if (!goals.Any())
        {
            await ReplyAsync(showAll
                ? "📭 No goals found."
                : "📭 No active goals. Add one with `!goal add <description>` or let Boiler create some automatically.");
            return;
        }

        var userGoals  = goals.Where(g => g.UserCreated).ToList();
        var boilerGoals = goals.Where(g => !g.UserCreated).ToList();

        var embed = new EmbedBuilder()
            .WithColor(new Color(0x2ECC71))
            .WithTitle($"🎯 Goals ({goals.Count} {(showAll ? "active/resolved" : "active")})")
            .WithFooter("!goal done <id> | !goal add <description>");

        if (userGoals.Any())
        {
            embed.AddField("📌 Your Goals",
                string.Join("\n", userGoals.Select(g => FormatGoalLine(g))),
                inline: false);
        }

        if (boilerGoals.Any())
        {
            embed.AddField("🐾 Boiler is tracking",
                string.Join("\n", boilerGoals.Select(g => FormatGoalLine(g))),
                inline: false);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("add")]
    [Alias("a", "new")]
    [Summary("Add a goal for Boiler to track. Usage: !goal add <description>")]
    public async Task AddGoalAsync([Remainder] string description)
    {
        await _memory.AddGoalAsync(
            Context.User.Id,
            description,
            reason: "Added by user",
            priority: 5,
            surfaceAfter: null);

        var embed = new EmbedBuilder()
            .WithColor(new Color(0x2ECC71))
            .WithTitle("🎯 Goal Added")
            .WithDescription(description)
            .WithFooter("Boiler will keep this in mind")
            .Build();

        await ReplyAsync(embed: embed);
    }

    [Command("done")]
    [Alias("resolve", "complete")]
    [Summary("Mark a goal as resolved. Usage: !goal done <id> [note]")]
    public async Task ResolveGoalAsync(int goalId, [Remainder] string? note = null)
    {
        await _memory.ResolveGoalAsync(Context.User.Id, goalId, note);
        await ReplyAsync($"✅ Goal `#{goalId}` resolved.");
    }

    [Command("drop")]
    [Alias("abandon", "del", "delete")]
    [Summary("Abandon a goal. Usage: !goal drop <id>")]
    public async Task AbandonGoalAsync(int goalId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var goal = await db.Goals.FirstOrDefaultAsync(g => g.Id == goalId && g.UserId == Context.User.Id);
        if (goal is null)
        {
            await ReplyAsync("❌ Goal not found.");
            return;
        }

        goal.Status = BotGoalStatus.Abandoned;
        goal.ResolvedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        await ReplyAsync($"🗑️ Goal `#{goalId}` abandoned.");
    }

    [Command("boiler")]
    [Alias("auto", "self")]
    [Summary("See only the goals Boiler created autonomously. Usage: !goal boiler")]
    public async Task BoilerGoalsAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var goals = await db.Goals
            .Where(g => g.UserId == Context.User.Id
                     && g.Status == BotGoalStatus.Active
                     && !g.UserCreated)
            .OrderByDescending(g => g.Priority)
            .ToListAsync();

        if (!goals.Any())
        {
            await ReplyAsync("🐾 Boiler hasn't created any autonomous goals yet — it needs more time to observe patterns.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithColor(new Color(0x9B59B6))
            .WithTitle($"🐾 Boiler's Self-Generated Goals ({goals.Count})")
            .WithDescription("These are things Boiler decided to track on its own, based on what it's observed about you.")
            .WithFooter("!goal done <id> to resolve | !goal drop <id> to dismiss");

        foreach (var g in goals)
        {
            var age = FormatAge(g.CreatedAt);
            var reason = string.IsNullOrWhiteSpace(g.Reason) ? "" : $"\n*Why: {g.Reason}*";
            embed.AddField(
                $"#{g.Id} — Priority {g.Priority}/10",
                $"{g.Description}{reason}\n*Created {age}*",
                inline: false);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("beliefs")]
    [Alias("worldview", "views")]
    [Summary("See what Boiler believes about you. Usage: !goal beliefs")]
    public async Task BeliefsAsync()
    {
        await Context.Channel.TriggerTypingAsync();
        var result = await _beliefs.FormatBeliefsForDisplayAsync(Context.User.Id);

        var embed = new EmbedBuilder()
            .WithColor(new Color(0xE67E22))
            .WithTitle("🧭 Boiler's Worldview")
            .WithDescription(result)
            .WithFooter("Beliefs form from patterns Boiler observes over time. They influence how Boiler responds to you.")
            .Build();

        await ReplyAsync(embed: embed);
    }

    // -------------------------------------------------------------------------
    //  Helpers
    // -------------------------------------------------------------------------

    private static string FormatGoalLine(BotGoal g)
    {
        var statusIcon = g.Status switch
        {
            BotGoalStatus.Resolved  => "✅",
            BotGoalStatus.Abandoned => "🗑️",
            _                       => g.Priority >= 8 ? "🔴" : g.Priority >= 5 ? "🟡" : "🟢"
        };
        return $"{statusIcon} `#{g.Id}` {g.Description}";
    }

    private static string FormatAge(DateTime dt)
    {
        var delta = DateTime.UtcNow - dt;
        if (delta.TotalHours < 1)  return "just now";
        if (delta.TotalDays  < 1)  return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays  < 7)  return $"{(int)delta.TotalDays}d ago";
        return dt.ToString("MMM d");
    }
}
