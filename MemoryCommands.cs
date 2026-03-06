using Discord.Commands;
using ProductivityBot.Services;

namespace ProductivityBot.Commands;

[Group("memory")]
[Alias("mem")]
[Summary("Manage Boiler's conversation memory")]
public class MemoryCommands : ModuleBase<SocketCommandContext>
{
    private readonly OllamaService _ollama;

    public MemoryCommands(OllamaService ollama) => _ollama = ollama;

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
}
