# Boiler-bot

A personal Discord productivity bot built with Discord.NET and C#.

## Features

- 📋 **Task Management** — add tasks, set due dates, priorities, mark done
- 🌿 **Habit Tracking** — daily/weekly habits with streak tracking
- ⏰ **Reminders** — one-shot reminders with flexible time input (`30m`, `2h`, `1d`, or datetime)

## Setup

### Prerequisites
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- A Discord bot token from the [Developer Portal](https://discord.com/developers/applications)

### 1. Configure the bot

Edit `config/appsettings.json`:

```json
{
  "Discord": {
    "Token": "YOUR_BOT_TOKEN_HERE",
    "Prefix": "!"
  },
  "Database": {
    "Path": "data/productivity.db"
  }
}
```

You can also set the token via environment variable to avoid storing it in the file:
```
BOT_Discord__Token=your_token_here
```

### 2. Discord Bot Settings (Developer Portal)

Under **Bot** settings, enable these **Privileged Gateway Intents**:
- ✅ Message Content Intent

Under **OAuth2 → URL Generator**, select:
- Scopes: `bot`
- Bot Permissions: `Send Messages`, `Read Message History`, `View Channels`

### 3. Run

```bash
dotnet run
```

Or build and run:
```bash
dotnet build
dotnet run --project ProductivityBot.csproj
```

## Usage

| Command | Description |
|---|---|
| `!help` | Show all commands |
| `!task add Buy milk` | Add a task |
| `!task add Fix bug --priority high --due 2025-03-15` | Add task with options |
| `!task list` | List pending tasks |
| `!task done 3` | Complete task #3 |
| `!habit add Morning workout` | Create a daily habit |
| `!habit log 1` | Log habit #1 as done today |
| `!habit today` | See unlogged habits for today |
| `!remind me 30m Take a break` | Reminder in 30 minutes |
| `!remind me 2h Check oven` | Reminder in 2 hours |

## Project Structure

```
ProductivityBot/
├── config/
│   └── appsettings.json       # Bot config (token, prefix)
├── src/
│   ├── Program.cs             # Entry point, DI setup
│   ├── Commands/
│   │   ├── GeneralCommands.cs # !help, !ping
│   │   ├── TaskCommands.cs    # !task *
│   │   ├── HabitCommands.cs   # !habit *
│   │   └── ReminderCommands.cs# !remind *
│   ├── Services/
│   │   ├── BotHostedService.cs     # Bot startup/shutdown
│   │   ├── CommandHandlerService.cs# Routes messages to commands
│   │   ├── TaskService.cs          # Task business logic
│   │   ├── HabitService.cs         # Habit + streak logic
│   │   └── ReminderService.cs      # Background reminder timer
│   ├── Models/
│   │   └── Models.cs          # EF Core entity models
│   └── Data/
│       └── BotDbContext.cs    # SQLite database context
└── ProductivityBot.csproj
```

## Next Steps / Ideas

- [ ] Slash commands (Discord's modern command style)
- [ ] Pomodoro timer (`!pomodoro start`)
- [ ] Weekly summary DM (cron-based)
- [ ] `!task edit <id>` command
- [ ] Notes/journal module
- [ ] Per-server vs per-user data separation
