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
    private readonly EodService _eodService;
    private readonly OllamaService _ollama;
    private readonly IConfiguration _config;
    private readonly ILogger<BotHostedService> _logger;

    public BotHostedService(
        DiscordSocketClient client,
        CommandHandlerService commandHandler,
        ReminderService reminderService,
        EodService eodService,
        OllamaService ollama,
        IConfiguration config,
        ILogger<BotHostedService> logger)
    {
        _client = client;
        _commandHandler = commandHandler;
        _reminderService = reminderService;
        _eodService = eodService;
        _ollama = ollama;
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
        _eodService.Stop();
        await _client.StopAsync();
        await _client.LogoutAsync();
    }

    private async Task OnReadyAsync()
    {
        _logger.LogInformation("Bot is connected as {Username}", _client.CurrentUser.Username);
        await _client.SetActivityAsync(new Game("!help | productivity mode", ActivityType.Playing));
        _reminderService.Start(_client);

        // Kick off model selection without blocking the Ready handler
        _ = Task.Run(RunModelSelectionAsync);
    }

    private async Task RunModelSelectionAsync()
    {
        // Give the client a moment to fully settle after Ready
        await Task.Delay(TimeSpan.FromSeconds(2));

        var ownerIdStr = _config["Discord:OwnerId"];
        if (!ulong.TryParse(ownerIdStr, out var ownerId))
        {
            _logger.LogWarning("Discord:OwnerId not configured — skipping model selection. !ask and !eod will be disabled.");
            _ollama.Disable();
            // Still start EOD service so it can report its disabled state cleanly
            _eodService.Start(_client);
            return;
        }

        IUser? owner;
        try
        {
            owner = await _client.GetUserAsync(ownerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not fetch owner user for model selection DM");
            _ollama.Disable();
            _eodService.Start(_client);
            return;
        }

        if (owner is null)
        {
            _logger.LogWarning("Owner user {Id} not found — !ask and !eod will be disabled.", ownerId);
            _ollama.Disable();
            _eodService.Start(_client);
            return;
        }

        var dmChannel = await owner.CreateDMChannelAsync();

        // Fetch available models
        var models = await _ollama.GetInstalledModelsAsync();

        if (models.Count == 0)
        {
            _ollama.Disable();
            await dmChannel.SendMessageAsync(
                "⚠️ **Boiler startup notice**\n" +
                "Couldn't reach Ollama or no models are installed.\n" +
                "`!ask` and `!eod` are **disabled** for this session. Install a model via `ollama pull <model>` and restart.");
            _eodService.Start(_client);
            return;
        }

        // Build the selection prompt
        var lines = new List<string>
        {
            "🐾 **Boiler is starting up!**",
            "The following Ollama models are installed. Reply with a number to select one, or `0` to disable `!ask` and `!eod`:\n"
        };

        for (int i = 0; i < models.Count; i++)
            lines.Add($"`{i + 1}.` {models[i]}");

        lines.Add("`0.` Disable !ask and !eod for this session");
        lines.Add("\n_You have 60 seconds to reply._");

        await dmChannel.SendMessageAsync(string.Join("\n", lines));

        // Wait for the owner's reply in the DM channel
        string? chosen = await WaitForOwnerReplyAsync(dmChannel, ownerId, models, TimeSpan.FromSeconds(60));

        if (chosen is null)
        {
            _ollama.Disable();
            await dmChannel.SendMessageAsync(
                "⏱️ No valid selection received. `!ask` and `!eod` are **disabled** for this session.\nRestart the bot to try again.");
            _eodService.Start(_client);
            return;
        }

        if (chosen == "disabled")
        {
            _ollama.Disable();
            await dmChannel.SendMessageAsync("🚫 `!ask` and `!eod` have been **disabled** for this session.");
            _eodService.Start(_client);
            return;
        }

        _ollama.SetModel(chosen);

        // Probe for tool call support
        await dmChannel.SendMessageAsync($"✅ Model set to **{chosen}**. `!ask` and `!eod` are ready to go!\n🔧 Testing tool call support...");
        var toolsSupported = await _ollama.ProbeToolSupportAsync();
        await dmChannel.SendMessageAsync(toolsSupported
            ? "🛠️ Tools enabled — `!ask` can manage tasks, habits, and reminders using natural language."
            : "⚠️ This model doesn't support tool calls — `!ask` will work conversationally only.\nTip: try `ollama pull qwen2.5:7b` or `llama3.1:8b` for tool use support.");

        // Start EOD after model is confirmed active
        _eodService.Start(_client);
    }

    /// <summary>
    /// Listens on the DM channel for a valid numbered reply from the owner.
    /// Returns the selected model name, "disabled" for 0, or null on timeout.
    /// </summary>
    private async Task<string?> WaitForOwnerReplyAsync(
        IDMChannel dmChannel,
        ulong ownerId,
        List<string> models,
        TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Task Handler(SocketMessage msg)
        {
            if (msg.Author.Id != ownerId) return Task.CompletedTask;
            if (msg.Channel.Id != dmChannel.Id) return Task.CompletedTask;

            if (int.TryParse(msg.Content.Trim(), out int choice))
            {
                if (choice == 0)
                    tcs.TrySetResult("disabled");
                else if (choice >= 1 && choice <= models.Count)
                    tcs.TrySetResult(models[choice - 1]);
                // Out of range — ignore, keep waiting
            }

            return Task.CompletedTask;
        }

        _client.MessageReceived += Handler;

        try
        {
            var timeoutTask = Task.Delay(timeout);
            var completedTask = await Task.WhenAny(tcs.Task, timeoutTask);
            return completedTask == timeoutTask ? null : await tcs.Task;
        }
        finally
        {
            _client.MessageReceived -= Handler;
        }
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