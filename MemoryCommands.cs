using Discord;
using Discord.Commands;
using ProductivityBot.Services;

namespace ProductivityBot.Commands;

[Group("memory")]
[Alias("mem")]
[Summary("Manage Boiler's conversation memory")]
public class MemoryCommands : ModuleBase<SocketCommandContext>
{
    private readonly OllamaService _ollama;
    private readonly MemoryService _memory;

    public MemoryCommands(OllamaService ollama, MemoryService memory)
    {
        _ollama = ollama;
        _memory = memory;
    }

    [Command("clear")]
    [Alias("reset", "forget", "wipe")]
    [Summary("Clear Boiler's memory of this conversation. Usage: !memory clear")]
    public async Task ClearAsync()
    {
        if (!_ollama.IsEnabled)
        {
            await ReplyAsync("🚫 The AI assistant is currently disabled.");
            return;
        }

        var before = _ollama.GetHistoryCount(Context.User.Id, Context.Channel.Id);

        if (before == 0)
        {
            await ReplyAsync("🐾 Nothing to forget — no conversation history in this channel yet.");
            return;
        }

        _ollama.ClearHistory(Context.User.Id, Context.Channel.Id);
        await ReplyAsync($"🧹 Cleared {before} messages from Boiler's memory in this channel. Fresh start!");
    }

    [Command("status")]
    [Alias("info", "count")]
    [Summary("Show how many messages Boiler remembers in this channel. Usage: !memory status")]
    public async Task StatusAsync()
    {
        if (!_ollama.IsEnabled)
        {
            await ReplyAsync("🚫 The AI assistant is currently disabled.");
            return;
        }

        var count = _ollama.GetHistoryCount(Context.User.Id, Context.Channel.Id);

        if (count == 0)
            await ReplyAsync("🐾 No conversation history in this channel yet.");
        else
            await ReplyAsync($"🧠 Boiler remembers **{count}** messages in this channel.");
    }

    [Command("recall")]
    [Alias("remember", "what")]
    [Summary("See what Boiler remembers about you. Usage: !memory recall [query]")]
    public async Task RecallAsync([Remainder] string? query = null)
    {
        await Context.Channel.TriggerTypingAsync();

        var result = await _memory.RecallAsync(Context.User.Id, query);

        // Split if needed for Discord's embed limit
        const int maxLen = 3800;
        if (result.Length <= maxLen)
        {
            var embed = new EmbedBuilder()
                .WithColor(new Color(0x9B59B6))
                .WithTitle(query is null ? "🧠 What Boiler Remembers" : $"🧠 Boiler's Memory: \"{query}\"")
                .WithDescription(result)
                .WithFooter("Use !memory clear to wipe conversation history")
                .Build();

            await ReplyAsync(embed: embed);
        }
        else
        {
            // Chunk it
            var chunks = SplitText(result, maxLen);
            for (int i = 0; i < chunks.Count; i++)
            {
                var embed = new EmbedBuilder()
                    .WithColor(new Color(0x9B59B6))
                    .WithTitle(i == 0
                        ? (query is null ? "🧠 What Boiler Remembers" : $"🧠 Boiler's Memory: \"{query}\"")
                        : "🧠 (continued)")
                    .WithDescription(chunks[i])
                    .WithFooter(i == chunks.Count - 1 ? "Use !memory clear to wipe conversation history" : $"Part {i + 1}/{chunks.Count}")
                    .Build();

                await ReplyAsync(embed: embed);
            }
        }
    }

    private static List<string> SplitText(string text, int maxLength)
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
