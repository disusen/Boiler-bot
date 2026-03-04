using System.Reflection;
using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ProductivityBot.Services;

public class CommandHandlerService
{
    private readonly DiscordSocketClient _client;
    private readonly CommandService _commands;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly ILogger<CommandHandlerService> _logger;

    public CommandHandlerService(
        DiscordSocketClient client,
        CommandService commands,
        IServiceProvider services,
        IConfiguration config,
        ILogger<CommandHandlerService> logger)
    {
        _client = client;
        _commands = commands;
        _services = services;
        _config = config;
        _logger = logger;
    }

    public async Task InitializeAsync()
    {
        _client.MessageReceived += HandleMessageAsync;
        _commands.CommandExecuted += OnCommandExecutedAsync;

        await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
        _logger.LogInformation("Loaded {Count} command modules", _commands.Modules.Count());
    }

    private async Task HandleMessageAsync(SocketMessage rawMessage)
    {
        if (rawMessage is not SocketUserMessage message) return;
        if (message.Author.IsBot) return;

        var prefix = _config["Discord:Prefix"] ?? "!";
        int argPos = 0;

        if (!message.HasStringPrefix(prefix, ref argPos) &&
            !message.HasMentionPrefix(_client.CurrentUser, ref argPos))
            return;

        var context = new SocketCommandContext(_client, message);
        await _commands.ExecuteAsync(context, argPos, _services);
    }

    private async Task OnCommandExecutedAsync(Optional<CommandInfo> command, ICommandContext context, IResult result)
    {
        if (result.IsSuccess) return;

        if (result.Error == CommandError.UnknownCommand) return;

        var errorMsg = result.Error switch
        {
            CommandError.BadArgCount => "Wrong number of arguments. Use `!help <command>` for usage.",
            CommandError.ParseFailed => "Couldn't parse your input. Check argument types.",
            CommandError.UnmetPrecondition => result.ErrorReason,
            _ => $"Error: {result.ErrorReason}"
        };

        await context.Channel.SendMessageAsync($"❌ {errorMsg}");
        _logger.LogWarning("Command error [{Error}]: {Reason}", result.Error, result.ErrorReason);
    }
}
