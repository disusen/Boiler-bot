using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ProductivityBot.Data;
using ProductivityBot.Models;

namespace ProductivityBot.Services;

public class ReminderService
{
    private readonly IServiceProvider _services;
    private readonly ILogger<ReminderService> _logger;
    private DiscordSocketClient? _client;
    private Timer? _timer;

    public ReminderService(IServiceProvider services, ILogger<ReminderService> logger)
    {
        _services = services;
        _logger = logger;
    }

    public void Start(DiscordSocketClient client)
    {
        _client = client;
        // Check for due reminders every 30 seconds
        _timer = new Timer(CheckRemindersCallback, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
        _logger.LogInformation("Reminder service started.");
    }

    public void Stop() => _timer?.Dispose();

    private async void CheckRemindersCallback(object? _)
    {
        if (_client is null) return;
        try
        {
            await CheckAndFireRemindersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in reminder check");
        }
    }

    private async Task CheckAndFireRemindersAsync()
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var now = DateTime.UtcNow;
        var due = db.Reminders
            .Where(r => !r.IsFired && r.FireAt <= now)
            .ToList();

        foreach (var reminder in due)
        {
            await FireReminderAsync(reminder);
            reminder.IsFired = true;
        }

        if (due.Count > 0)
            await db.SaveChangesAsync();
    }

    private async Task FireReminderAsync(Reminder reminder)
    {
        if (_client is null) return;

        try
        {
			IMessageChannel? channel = _client.GetChannel(reminder.ChannelId) as IMessageChannel;
			if (channel is null)
			{
				var user = await _client.GetUserAsync(reminder.UserId);
				if (user is not null)
					channel = await user.CreateDMChannelAsync();
			}

			if (channel is not null)
                await channel.SendMessageAsync($"⏰ <@{reminder.UserId}> Reminder: **{reminder.Message}**");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fire reminder {Id}", reminder.Id);
        }
    }

    public async Task<Reminder> AddReminderAsync(ulong userId, ulong channelId, string message, DateTime fireAt)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();

        var reminder = new Reminder
        {
            UserId = userId,
            ChannelId = channelId,
            Message = message,
            FireAt = fireAt
        };
        db.Reminders.Add(reminder);
        await db.SaveChangesAsync();
        return reminder;
    }

    public async Task<List<Reminder>> GetPendingRemindersAsync(ulong userId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        return await db.Reminders
            .Where(r => r.UserId == userId && !r.IsFired && r.FireAt > DateTime.UtcNow)
            .OrderBy(r => r.FireAt)
            .ToListAsync();
    }

    public async Task<bool> CancelReminderAsync(ulong userId, int reminderId)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
        var reminder = db.Reminders.FirstOrDefault(r => r.Id == reminderId && r.UserId == userId && !r.IsFired);
        if (reminder is null) return false;
        db.Reminders.Remove(reminder);
        await db.SaveChangesAsync();
        return true;
    }
}