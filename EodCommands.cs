using Discord.Commands;
using Microsoft.Extensions.Configuration;
using ProductivityBot.Services;

namespace ProductivityBot.Commands;

[Summary("End-of-day commands")]
public class EodCommands : ModuleBase<SocketCommandContext>
{
    private readonly EodService _eod;
    private readonly IConfiguration _config;

    public EodCommands(EodService eod, IConfiguration config)
    {
        _eod = eod;
        _config = config;
    }

    [Command("eod")]
    [Summary("Trigger your end-of-day summary early. Owner only.")]
    public async Task EodAsync()
    {
        // Owner-only check
        if (!ulong.TryParse(_config["Discord:OwnerId"], out var ownerId) ||
            Context.User.Id != ownerId)
        {
            await ReplyAsync("🔒 This command is only available to the bot owner.");
            return;
        }

        var error = await _eod.TriggerManualEodAsync();

        if (error is not null)
        {
            await ReplyAsync(error);
            return;
        }

        await ReplyAsync("🌙 Got it — generating your end-of-day summary and sending it to your DMs shortly!");
    }
}
