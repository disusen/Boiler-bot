using Discord;
using Discord.Commands;
using ProductivityBot.Services;

namespace ProductivityBot.Commands;

[Summary("General commands")]
public class GeneralCommands : ModuleBase<SocketCommandContext>
{
    private readonly CommandService _commands;
    private readonly MemoryService _memory;
    private readonly BeliefService _beliefs;
    private readonly OllamaService _ollama;

    public GeneralCommands(CommandService commands, MemoryService memory, BeliefService beliefs, OllamaService ollama)
    {
        _commands = commands;
        _memory = memory;
        _beliefs = beliefs;
        _ollama = ollama;
    }

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
            .AddField("🤖 AI Assistant",
                "`!ask <question>` — ask Boiler anything\n" +
                "`!memory clear` — wipe conversation history\n" +
                "`!memory status` — messages in context\n" +
                "`!memory recall [query]` — see what Boiler remembers")
            .AddField("🎯 Goals",
                "`!goal list` — active goals\n" +
                "`!goal add <description>` — add a goal\n" +
                "`!goal done <id>` — resolve a goal\n" +
                "`!goal drop <id>` — abandon a goal\n" +
                "`!goal boiler` — Boiler's self-generated goals\n" +
                "`!goal beliefs` — Boiler's worldview")
            .AddField("🌙 End of Day",
                "`!eod` — trigger your daily summary early _(owner only)_\n" +
                "Boiler automatically sends a summary at **9pm** in the configured timezone.")
            .AddField("🐾 Boiler",
                "`!boiler` — Boiler's current state, mood, and what it's thinking about")
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

    [Command("boiler")]
    [Summary("See Boiler's current internal state — mood, thoughts, and worldview summary")]
    public async Task BoilerStateAsync()
    {
        await Context.Channel.TriggerTypingAsync();

        var state = await _memory.GetOrCreateStateAsync(Context.User.Id);
        var beliefs = await _beliefs.GetActiveBeliefsAsync(Context.User.Id);
        var model = _ollama.CurrentModel ?? "no model selected";

        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"**Mood:** {state.CurrentMood}");

        if (!string.IsNullOrWhiteSpace(state.CurrentThought))
            sb.AppendLine($"**On my mind:** {state.CurrentThought}");

        if (!string.IsNullOrWhiteSpace(state.RecentObservation))
            sb.AppendLine($"**Recently noticed:** {state.RecentObservation}");

        // Day streak context
        if (state.ConsecutiveGoodDays >= 3)
            sb.AppendLine($"**Streak:** {state.ConsecutiveGoodDays} good days in a row 🔥");
        else if (state.ConsecutiveRoughDays >= 2)
            sb.AppendLine($"**Streak:** {state.ConsecutiveRoughDays} rough days — keeping an eye on you");

        sb.AppendLine();

        if (beliefs.Any())
        {
            sb.AppendLine($"**Active beliefs about you:** {beliefs.Count}");
            foreach (var b in beliefs.Take(3))
                sb.AppendLine($"• {b.Claim} *(confidence: {b.Confidence:P0})*");
            if (beliefs.Count > 3)
                sb.AppendLine($"*...and {beliefs.Count - 3} more. Use `!goal beliefs` for the full list.*");
        }
        else
        {
            sb.AppendLine("*No strong beliefs formed yet — still learning your patterns.*");
        }

        var embed = new EmbedBuilder()
            .WithColor(new Color(0xE67E22))
            .WithAuthor("Boiler", iconUrl: Context.Client.CurrentUser.GetAvatarUrl())
            .WithDescription(sb.ToString().Trim())
            .WithFooter($"Model: {model} • Last updated: {state.LastUpdatedAt:MMM d, HH:mm} UTC")
            .Build();

        await ReplyAsync(embed: embed);
    }
}
