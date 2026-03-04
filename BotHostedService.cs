using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductivityBot.Services;

namespace ProductivityBot.Services;

public class BotHostedService : IHostedService
{
    private readonly DiscordSocketClient _client;
    private readonly CommandHandlerService _commandHandler;
    private readonly ReminderService _reminderService;
    private readonly IConfiguration _config;
    private readonly ILogger<BotHostedService> _logger;

    public BotHostedService(
        DiscordSocketClient client,
        CommandHandlerService commandHandler,
        ReminderService reminderService,
        IConfiguration config,
        ILogger<BotHostedService> logger)
    {
        _client = client;
        _commandHandler = commandHandler;
        _reminderService = reminderService;
        _config = config;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _client.Log += LogAsync;
        _client.Ready += OnReadyAsync;

        await _commandHandler.InitializeAsync();

        var token = _config["Discord:Token"]
            ?? throw new InvalidOperationException("Discord token not configured.");

        await _client.LoginAsync(TokenType.Bot, token);
        await _client.StartAsync();
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _reminderService.Stop();
        await _client.StopAsync();
        await _client.LogoutAsync();
    }

    private async Task OnReadyAsync()
    {
        _logger.LogInformation("Bot is connected as {Username}", _client.CurrentUser.Username);
        await _client.SetActivityAsync(new Game("!help | productivity mode", ActivityType.Playing));
        _reminderService.Start(_client);
    }

    private Task LogAsync(LogMessage log)
    {
        var level = log.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            _ => LogLevel.Debug
        };
        _logger.Log(level, log.Exception, "[Discord] {Message}", log.Message);
        return Task.CompletedTask;
    }
}
