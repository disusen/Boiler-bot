using Discord;
using Discord.Commands;
using ProductivityBot.Services;

namespace ProductivityBot.Commands;

[Group("remind")]
[Alias("r", "reminder")]
[Summary("Reminder commands")]
public class ReminderCommands : ModuleBase<SocketCommandContext>
{
    private readonly ReminderService _reminders;

    public ReminderCommands(ReminderService reminders) => _reminders = reminders;

    [Command("me")]
    [Summary("Set a reminder. Usage: !remind me <time> <message>\nTime examples: 30m, 2h, 1d, 2025-03-10 15:00")]
    public async Task RemindMeAsync(string timeStr, [Remainder] string message)
    {
        var fireAt = ParseTime(timeStr);

        if (fireAt is null)
        {
            await ReplyAsync("❌ Couldn't parse that time. Try `30m`, `2h`, `1d`, or a date like `2025-03-10 15:00`");
            return;
        }

        if (fireAt < DateTime.UtcNow.AddSeconds(10))
        {
            await ReplyAsync("❌ That time is in the past.");
            return;
        }

        var reminder = await _reminders.AddReminderAsync(
            Context.User.Id,
            Context.Channel.Id,
            message,
            fireAt.Value);

        var timeUntil = fireAt.Value - DateTime.UtcNow;
        var timeStr2 = FormatTimeSpan(timeUntil);

        var embed = new EmbedBuilder()
            .WithColor(Color.Purple)
            .WithTitle("⏰ Reminder Set")
            .AddField("Message", message)
            .AddField("Fires In", timeStr2, inline: true)
            .AddField("At (UTC)", fireAt.Value.ToString("MMM d, HH:mm"), inline: true)
            .WithFooter($"Reminder ID: {reminder.Id} | !remind list to see all")
            .Build();

        await ReplyAsync(embed: embed);
    }

    [Command("list")]
    [Alias("ls")]
    [Summary("List your pending reminders")]
    public async Task ListRemindersAsync()
    {
        var reminders = await _reminders.GetPendingRemindersAsync(Context.User.Id);

        if (reminders.Count == 0)
        {
            await ReplyAsync("📭 No pending reminders.");
            return;
        }

        var embed = new EmbedBuilder()
            .WithColor(Color.Purple)
            .WithTitle($"⏰ Your Reminders ({reminders.Count})");

        foreach (var r in reminders)
        {
            var timeUntil = r.FireAt - DateTime.UtcNow;
            embed.AddField(
                $"#{r.Id} — {r.FireAt:MMM d, HH:mm UTC}",
                $"{r.Message}\nFires in: {FormatTimeSpan(timeUntil)}",
                inline: false);
        }

        await ReplyAsync(embed: embed.Build());
    }

    [Command("cancel")]
    [Alias("del", "delete")]
    [Summary("Cancel a reminder. Usage: !remind cancel <id>")]
    public async Task CancelReminderAsync(int reminderId)
    {
        var success = await _reminders.CancelReminderAsync(Context.User.Id, reminderId);

        if (!success)
            await ReplyAsync("❌ Reminder not found.");
        else
            await ReplyAsync($"🗑️ Reminder `#{reminderId}` cancelled.");
    }

    // --- Helpers ---

    private static DateTime? ParseTime(string input)
    {
        input = input.Trim().ToLower();

        // Relative: 30m, 2h, 1d, 1h30m
        if (TryParseRelative(input, out var relative))
            return DateTime.UtcNow.Add(relative);

        // Absolute: 2025-03-10, 2025-03-10 15:00
        if (DateTime.TryParse(input, out var absolute))
            return absolute.ToUniversalTime();

        return null;
    }

    private static bool TryParseRelative(string input, out TimeSpan result)
    {
        result = TimeSpan.Zero;
        var total = TimeSpan.Zero;
        int i = 0;

        while (i < input.Length)
        {
            if (!char.IsDigit(input[i])) break;
            int j = i;
            while (j < input.Length && char.IsDigit(input[j])) j++;
            if (!int.TryParse(input[i..j], out int num)) break;
            if (j >= input.Length) break;

            char unit = input[j];
            total += unit switch
            {
                'm' => TimeSpan.FromMinutes(num),
                'h' => TimeSpan.FromHours(num),
                'd' => TimeSpan.FromDays(num),
                _ => TimeSpan.Zero
            };

            if (unit is not ('m' or 'h' or 'd')) break;
            i = j + 1;
        }

        if (total == TimeSpan.Zero) return false;
        result = total;
        return true;
    }

    private static string FormatTimeSpan(TimeSpan ts)
    {
        if (ts.TotalDays >= 1) return $"{(int)ts.TotalDays}d {ts.Hours}h";
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}h {ts.Minutes}m";
        return $"{ts.Minutes}m";
    }
}
