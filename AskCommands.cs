using Discord;
using Discord.Commands;
using ProductivityBot.Services;

namespace ProductivityBot.Commands;

[Summary("AI assistant commands")]
public class AskCommands : ModuleBase<SocketCommandContext>
{
    private readonly OllamaService _ollama;

    public AskCommands(OllamaService ollama) => _ollama = ollama;

    [Command("ask")]
    [Summary("Ask Boiler a question. Boiler remembers this conversation. Usage: !ask <question>")]
    public async Task AskAsync([Remainder] string prompt)
    {
        if (!_ollama.IsEnabled)
        {
            await ReplyAsync(
                "🚫 The AI assistant is currently disabled.\n" +
                "No Ollama model was selected at startup. The bot owner can restart to configure one.");
            return;
        }

        await Context.Channel.TriggerTypingAsync();

        var thinking = await ReplyAsync("🐾 Boiler is thinking...");

        var response = await _ollama.AskAsync(
            Context.User.Id,
            Context.Channel.Id,
            prompt);

        var modelLabel = _ollama.CurrentModel ?? "unknown";
        var historyCount = _ollama.GetHistoryCount(Context.User.Id, Context.Channel.Id);

        if (response.Length <= 1900)
        {
            var embed = new EmbedBuilder()
                .WithColor(new Color(0x5865F2))
                .WithAuthor("Boiler", iconUrl: Context.Client.CurrentUser.GetAvatarUrl())
                .WithDescription(response)
                .WithFooter($"Asked by {Context.User.Username} • {modelLabel} • {historyCount} msgs in context")
                .Build();

            await thinking.DeleteAsync();
            await ReplyAsync(embed: embed);
        }
        else
        {
            await thinking.DeleteAsync();
            var chunks = SplitResponse(response, 1900);
            for (int i = 0; i < chunks.Count; i++)
            {
                var embed = new EmbedBuilder()
                    .WithColor(new Color(0x5865F2))
                    .WithAuthor(i == 0 ? "Boiler" : "Boiler (continued)", iconUrl: Context.Client.CurrentUser.GetAvatarUrl())
                    .WithDescription(chunks[i])
                    .WithFooter(i == chunks.Count - 1
                        ? $"Asked by {Context.User.Username} • {modelLabel} • {historyCount} msgs in context"
                        : $"Part {i + 1}/{chunks.Count}")
                    .Build();

                await ReplyAsync(embed: embed);
            }
        }
    }



    private static List<string> SplitResponse(string text, int maxLength)
    {
        var chunks = new List<string>();
        int index = 0;

        while (index < text.Length)
        {
            int length = Math.Min(maxLength, text.Length - index);

            if (index + length < text.Length)
            {
                int splitAt = text.LastIndexOf('\n', index + length, length);
                if (splitAt == -1) splitAt = text.LastIndexOf(' ', index + length, length);
                if (splitAt > index) length = splitAt - index;
            }

            chunks.Add(text.Substring(index, length).Trim());
            index += length;
        }

        return chunks;
    }
}
