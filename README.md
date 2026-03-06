# Boiler-bot

A personal Discord productivity bot built with Discord.NET and C#, named after a good boy called Boiler 🐾

## Features

- 📋 **Task Management** - add tasks, set due dates, priorities, mark done
- 🌿 **Habit Tracking** - daily/weekly habits with streak tracking
- ⏰ **Reminders** - one-shot reminders with flexible time input (`30m`, `2h`, `1d`, or datetime)
- 🤖 **Local AI Assistant** - ask anything via a locally running LLM through Ollama, no API keys or cloud required
- 🌙 **End-of-Day Summary** - AI-generated daily recap of tasks and habits delivered to your DMs at 9pm, or on demand with `!eod`

## Setup

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- A Discord bot token from the [Developer Portal](https://discord.com/developers/applications)
- [Ollama](https://ollama.com) for the `!ask` and `!eod` commands

### 1. Install Ollama and pull a model

Download and install Ollama from [ollama.com](https://ollama.com), then pull whichever model you want to use. For example:

```powershell
ollama pull phi4
```

Or a lighter alternative if you're constrained on VRAM:

```powershell
ollama pull llama3.2:3b
```

Once pulled, Ollama runs in the background automatically and exposes a local API at `http://localhost:11434`. On startup, the bot will DM you a list of all installed models and ask you to pick one. You can also select `0` to disable `!ask` and `!eod` for that session.

> **Note:** Phi-4 14B (~9GB) requires a GPU with at least 12GB VRAM for comfortable performance. Smaller models like `llama3.2:3b` run on modest hardware with minimal quality tradeoff for simple queries.

### 2. Configure the bot

Edit `config/appsettings.json`:

```json
{
  "Discord": {
    "Token": "YOUR_BOT_TOKEN_HERE",
    "Prefix": "!",
    "OwnerId": "YOUR_DISCORD_USER_ID_HERE"
  },
  "Database": {
    "Path": "data/productivity.db"
  },
  "Ollama": {
    "BaseUrl": "http://localhost:11434"
  },
  "Bot": {
    "Timezone": "Europe/London"
  }
}
```

**Timezone** controls when the automatic 9pm EOD summary fires. Use IANA timezone IDs on Linux/macOS (e.g. `Europe/Vilnius`, `America/New_York`) or Windows timezone names on Windows (e.g. `Eastern Standard Time`). Defaults to `UTC` if not set or unrecognised.

To get your Discord user ID: go to **Settings → Advanced**, enable **Developer Mode**, then right-click your username anywhere and select **Copy User ID**.

To avoid storing the token in a file, create `config/appsettings.local.json` (gitignored) with just:

```json
{
  "Discord": {
    "Token": "your_real_token_here"
  }
}
```

Or use an environment variable:

```
BOT_Discord__Token=your_token_here
```

### 3. Discord Bot Settings (Developer Portal)

Under **Bot** settings, enable these **Privileged Gateway Intents**:
- ✅ Message Content Intent

Under **OAuth2 → URL Generator**, select:
- Scopes: `bot`
- Bot Permissions: `Send Messages`, `Read Message History`, `View Channels`

### 4. Run

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
| **Tasks** | |
| `!task add Buy milk` | Add a task |
| `!task add Fix bug --priority high --due 2025-03-15` | Add task with priority and due date |
| `!task list` | List pending tasks |
| `!task done 3` | Complete task #3 |
| `!task del 3` | Delete task #3 |
| `!task stats` | Show your task statistics |
| **Habits** | |
| `!habit add Morning workout` | Create a daily habit |
| `!habit list` | List habits with streak info |
| `!habit log 1` | Log habit #1 as done today |
| `!habit today` | See unlogged habits for today |
| `!habit archive 1` | Archive a habit |
| **Reminders** | |
| `!remind me 30m Take a break` | Reminder in 30 minutes |
| `!remind me 2h Check oven` | Reminder in 2 hours |
| `!remind me 1d Stand up meeting` | Reminder in 1 day |
| `!remind list` | List pending reminders |
| `!remind cancel 2` | Cancel reminder #2 |
| **AI Assistant** | |
| `!ask <question>` | Ask your selected Ollama model anything — runs fully locally |
| **End of Day** | |
| `!eod` | Trigger your end-of-day summary early _(owner only)_ |

## End-of-Day Summary

At **9pm in your configured timezone**, Boiler will automatically DM you an AI-generated summary of your day covering:

- ✅ Tasks completed today
- ➕ Tasks added today
- ⬜ Tasks still pending (with overdue warnings)
- ✅ Habits logged and current streaks
- ❌ Habits missed

You can also call `!eod` at any time before 9pm to trigger it early. Only one summary is sent per day — if you use `!eod`, the automatic 9pm trigger is skipped. The daily flag resets at midnight in your configured timezone.

EOD requires Ollama to be active. If no model was selected at startup, `!eod` is disabled for that session.

### Upgrading an existing database

If you're updating from a version before EOD was added, the `EodLogs` table won't exist in your database. Create it manually with:

```bash
sqlite3 data/productivity.db "CREATE TABLE IF NOT EXISTS 'EodLogs' ('Id' INTEGER NOT NULL CONSTRAINT 'PK_EodLogs' PRIMARY KEY AUTOINCREMENT, 'Date' TEXT NOT NULL, 'FiredByCommand' INTEGER NOT NULL, 'FiredAt' TEXT NOT NULL); CREATE UNIQUE INDEX IF NOT EXISTS 'IX_EodLogs_Date' ON 'EodLogs' ('Date');"
```

Then restart the bot. Going forward, schema migrations will be handled properly via EF Core migrations.

## Project Structure

```
Boiler-bot/
├── config/
│   ├── appsettings.json          # Bot config (committed, no secrets)
│   └── appsettings.local.json    # Your real token (gitignored)
├── src/
│   ├── Program.cs                # Entry point, DI setup
│   ├── Commands/
│   │   ├── GeneralCommands.cs    # !help, !ping
│   │   ├── TaskCommands.cs       # !task *
│   │   ├── HabitCommands.cs      # !habit *
│   │   ├── ReminderCommands.cs   # !remind *
│   │   ├── AskCommands.cs        # !ask
│   │   └── EodCommands.cs        # !eod
│   ├── Services/
│   │   ├── BotHostedService.cs        # Bot startup/shutdown + model selection
│   │   ├── CommandHandlerService.cs   # Routes messages to commands
│   │   ├── TaskService.cs             # Task business logic
│   │   ├── HabitService.cs            # Habit + streak logic
│   │   ├── ReminderService.cs         # Background reminder timer
│   │   ├── OllamaService.cs           # Ollama HTTP client
│   │   └── EodService.cs              # End-of-day summary logic + timer
│   ├── Models/
│   │   └── Models.cs             # EF Core entity models
│   └── Data/
│       └── BotDbContext.cs       # SQLite database context
└── ProductivityBot.csproj
```

## Next Steps / Ideas

- [ ] Slash commands (Discord's modern `/command` style)
- [ ] Pomodoro timer (`!pomodoro start`)
- [ ] `!task edit <id>` command
- [ ] Notes/journal module
- [ ] Conversation memory for `!ask` (multi-turn context)
- [ ] Per-server vs per-user data separation
- [ ] EF Core migrations for schema changes