using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ProductivityBot.Data;
using ProductivityBot.Services;

namespace ProductivityBot;

class Program
{
    static async Task Main(string[] args)
    {
        var host = Host.CreateDefaultBuilder(args)
            .ConfigureAppConfiguration(config =>
            {
                config.SetBasePath(AppContext.BaseDirectory);
                config.AddJsonFile("config/appsettings.json", optional: false, reloadOnChange: true);
                config.AddJsonFile("config/appsettings.local.json", optional: true, reloadOnChange: true);
                config.AddEnvironmentVariables(prefix: "BOT_");
            })
            .ConfigureServices((context, services) =>
            {
                // Discord client
                services.AddSingleton(new DiscordSocketClient(new DiscordSocketConfig
                {
                    LogLevel = LogSeverity.Info,
                    GatewayIntents = GatewayIntents.Guilds
                        | GatewayIntents.GuildMessages
                        | GatewayIntents.MessageContent
                        | GatewayIntents.DirectMessages
                }));

                services.AddSingleton<CommandService>(new CommandService(new CommandServiceConfig
                {
                    LogLevel = LogSeverity.Info,
                    CaseSensitiveCommands = false
                }));

                // Database
                var dbPath = context.Configuration["Database:Path"] ?? "data/productivity.db";
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
                services.AddDbContext<BotDbContext>(options =>
                    options.UseSqlite($"Data Source={dbPath}"));

                // Bot services
                services.AddSingleton<CommandHandlerService>();
                services.AddScoped<TaskService>();
                services.AddScoped<HabitService>();
                services.AddSingleton<ReminderService>();
                services.AddSingleton<OllamaService>();
                services.AddSingleton<EodService>();
                services.AddSingleton<RamblingService>();

                // Hosted service that runs the bot
                services.AddHostedService<BotHostedService>();
            })
            .ConfigureLogging(logging =>
            {
                logging.ClearProviders();
                logging.AddConsole();
            })
            .Build();

        // Apply any pending EF Core migrations on startup
        using (var scope = host.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<BotDbContext>();
            await db.Database.MigrateAsync();
        }

        await host.RunAsync();
    }
}