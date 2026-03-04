using Discord;
using Discord.Commands;
using ProductivityBot.Models;
using ProductivityBot.Services;

namespace ProductivityBot.Commands;

[Group("habit")]
[Alias("h")]
[Summary("Habit tracking commands")]
public class HabitCommands : ModuleBase<SocketCommandContext>
{
    private readonly HabitService _habits;

    public HabitCommands(HabitService habits) => _habits = habits;

    [Command("add")]
    [Alias("a", "new")]
    [Summary("Add a new habit. Usage: !habit add <name> [--desc <description>] [--freq daily|weekly]")]
    public async Task AddHabitAsync([Remainder] string input)
    {
        var parsed = ParseHabitInput(input);

        var habit = await _habits.AddHabitAsync(
            Context.User.Id,
            parsed.name,
            parsed.description,
            parsed.frequency);

        var embed = new EmbedBuilder()
            .WithColor(Color.Green)
            .WithTitle("🌱 Habit Added")
            .AddField("Habit", $"`#{habit.Id}` {habit.Name}", inline: false)
            .AddField("Frequency", habit.Frequency.ToString(), inline: true)
            .WithFooter("Use !habit log <id> to check in daily")
            .Build();

        await ReplyAsync(embed: embed);
    }

    [Command("list")]
    [Alias("l", "ls")]
    [Summary("List your active habits with streaks")]
    public async Task ListHabitsAsync()
    {
        var stats = await _habits.GetHabitStatsAsync(Context.User.Id);

        if (stats.Count == 0)
        {
            await ReplyAsync("🌱 No habits yet. Start one with `!habit add <name>`");
            return;
        }

        var today = DateTime.UtcNow.Date;
        var embed = new EmbedBuilder()
            .WithColor(Color.Green)
            .WithTitle($"🌿 Your Habits ({stats.Count} active)")
            .WithFooter("!habit log <id> to check in");

        foreach (var (habit, streak, totalLogs) in stats)
        {
            bool loggedToday = habit.Logs.Any(l => l.LoggedAt.Date == today);
            var streakStr = streak > 0 ? $"🔥 {streak} day streak" : "No streak yet";
            var statusIcon = loggedToday ? "✅" : "⬜";

            embed.AddField(
                $"{statusIcon} #{habit.Id} {habit.Name}",
                $"{streakStr} • {totalLogs} total logs",
                inline: false);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("log")]
    [Alias("check", "done", "tick")]
    [Summary("Log a habit completion. Usage: !habit log <id> [note]")]
    public async Task LogHabitAsync(int habitId, [Remainder] string? note = null)
    {
        var (success, message) = await _habits.LogHabitAsync(Context.User.Id, habitId, note);

        if (!success)
        {
            await ReplyAsync($"❌ {message}");
            return;
        }

        var habit = await _habits.GetHabitAsync(Context.User.Id, habitId);
        var streak = habit?.GetCurrentStreak() ?? 0;

        var embed = new EmbedBuilder()
            .WithColor(Color.Green)
            .WithTitle($"✅ {habit?.Name ?? "Habit"} logged!")
            .WithDescription(message)
            .WithFooter($"Keep it up! Total logs: {habit?.Logs.Count ?? 1}")
            .Build();

        await ReplyAsync(embed: embed);
    }

    [Command("today")]
    [Alias("check-in", "pending")]
    [Summary("Show which habits you haven't logged today")]
    public async Task TodayAsync()
    {
        var unlogged = await _habits.GetUnloggedTodayAsync(Context.User.Id);

        if (unlogged.Count == 0)
        {
            await ReplyAsync("🎉 All habits logged for today! Great work!");
            return;
        }

        var embed = new EmbedBuilder()
            .WithColor(Color.Orange)
            .WithTitle($"📋 Pending Habits Today ({unlogged.Count})")
            .WithDescription(string.Join("\n", unlogged.Select(h => $"⬜ `#{h.Id}` {h.Name}")))
            .WithFooter("!habit log <id> to check in")
            .Build();

        await ReplyAsync(embed: embed);
    }

    [Command("archive")]
    [Alias("delete", "remove")]
    [Summary("Archive a habit. Usage: !habit archive <id>")]
    public async Task ArchiveHabitAsync(int habitId)
    {
        var success = await _habits.ArchiveHabitAsync(Context.User.Id, habitId);

        if (!success)
            await ReplyAsync("❌ Habit not found.");
        else
            await ReplyAsync($"📦 Habit `#{habitId}` archived.");
    }

    // --- Helpers ---

    private static (string name, string? description, HabitFrequency frequency) ParseHabitInput(string input)
    {
        string name = input;
        string? description = null;
        var frequency = HabitFrequency.Daily;

        var parts = input.Split("--");
        name = parts[0].Trim();

        for (int i = 1; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (part.StartsWith("desc ", StringComparison.OrdinalIgnoreCase))
                description = part[5..].Trim();
            else if (part.StartsWith("freq ", StringComparison.OrdinalIgnoreCase))
            {
                var f = part[5..].Trim().ToLower();
                frequency = f switch
                {
                    "weekly" or "w" => HabitFrequency.Weekly,
                    _ => HabitFrequency.Daily
                };
            }
        }

        return (name, description, frequency);
    }
}
