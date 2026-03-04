using Discord;
using Discord.Commands;

namespace ProductivityBot.Commands;

[Summary("General commands")]
public class GeneralCommands : ModuleBase<SocketCommandContext>
{
    private readonly CommandService _commands;

    public GeneralCommands(CommandService commands) => _commands = commands;

    [Command("help")]
    [Summary("Show available commands")]
    public async Task HelpAsync([Remainder] string? commandName = null)
    {
        if (commandName is not null)
        {
            var result = _commands.Search(Context, commandName);
            if (!result.IsSuccess || result.Commands.Count == 0)
            {
                await ReplyAsync($"❌ No command found matching `{commandName}`.");
                return;
            }

            var embed = new EmbedBuilder().WithColor(Color.Blue).WithTitle($"Help: {commandName}");
            foreach (var match in result.Commands)
            {
                var cmd = match.Command;
                embed.AddField(
                    string.Join(", ", new[] { cmd.Name }.Concat(cmd.Aliases).Select(a => $"`!{a}`")),
                    cmd.Summary ?? "No description.");
            }
            await ReplyAsync(embed: embed.Build());
            return;
        }

        var helpEmbed = new EmbedBuilder()
            .WithColor(Color.Blue)
            .WithTitle("📖 BoilerBot Help")
            .WithDescription("Your personal assistant bot.\nUse `!help <command>` for details.")
            .AddField("📋 Tasks",
                "`!task add <title>` — add a task\n" +
                "`!task list` — show pending tasks\n" +
                "`!task done <id>` — complete a task\n" +
                "`!task del <id>` — delete a task\n" +
                "`!task stats` — your stats")
            .AddField("🌿 Habits",
                "`!habit add <name>` — create a habit\n" +
                "`!habit list` — list habits + streaks\n" +
                "`!habit log <id>` — log completion\n" +
                "`!habit today` — see unlogged habits\n" +
                "`!habit archive <id>` — archive a habit")
            .AddField("⏰ Reminders",
                "`!remind me <time> <msg>` — set a reminder\n" +
                "`!remind list` — pending reminders\n" +
                "`!remind cancel <id>` — cancel a reminder\n" +
                "Time formats: `30m`, `2h`, `1d`, `2025-03-10 15:00`")
            .WithFooter("!task add Buy groceries | !habit add Morning workout | !remind me 1h Take a break")
            .Build();

        await ReplyAsync(embed: helpEmbed);
    }

    [Command("ping")]
    [Summary("Check bot latency")]
    public async Task PingAsync()
    {
        await ReplyAsync($"🏓 Pong! Latency: `{Context.Client.Latency}ms`");
    }
}
