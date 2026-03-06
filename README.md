# Boiler-bot

A personal Discord productivity bot built with Discord.NET and C#, named after a good boy called Boiler 🐾

## Features

- 📋 **Task Management** - add tasks, set due dates, priorities, mark done
- 🌿 **Habit Tracking** - daily/weekly habits with streak tracking
- ⏰ **Reminders** - one-shot reminders with flexible time input (`30m`, `2h`, `1d`, or datetime)
- 🤖 **Local AI Assistant** - ask anything via a locally running LLM through Ollama, with per-channel conversation memory - no API keys or cloud required
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
    "BaseUrl": "http://localhost:11434",
    "ContextMessages": 20
  },
  "Bot": {
    "Timezone": "Europe/London"
  }
}
```

**`Ollama:ContextMessages`** - how many messages Boiler remembers per conversation (default: `20`). Each exchange (your message + Boiler's reply) counts as 2. With Phi-4's 16k context window, 20 is well within safe limits.

**`Bot:Timezone`** — controls when the automatic 9pm EOD summary fires. Use IANA timezone IDs on Linux/macOS (e.g. `Europe/Vilnius`, `America/New_York`) or Windows timezone names on Windows (e.g. `Eastern Standard Time`). Defaults to `UTC` if not set or unrecognised.

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

### 4. Install EF Core tools and create the initial migration

```bash
dotnet tool install --global dotnet-ef
dotnet ef migrations add InitialCreate
```

This generates a `Migrations/` folder that tracks your schema. You only need to do this once on first setup. The bot applies pending migrations automatically on every startup via `MigrateAsync()`.

### 5. Run

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
| `!ping` | Check bot latency |
| **Tasks** | |
| `!task add Buy milk` | Add a task |
| `!task add Fix bug --priority high --due 2025-03-15` | Add task with priority and due date |
| `!task list` | List pending tasks |
| `!task done 3` | Complete task #3 |
| `!task del 3` | Delete task #3 |
| `!task stats` | Show your task statistics |
| **Habits** | |
| `!habit add Morning workout` | Create a daily habit |
| `!habit add Read --freq weekly` | Create a weekly habit |
| `!habit list` | List habits with streak info |
| `!habit log 1` | Log habit #1 as done today |
| `!habit log 1 felt great` | Log with a note |
| `!habit today` | See unlogged habits for today |
| `!habit archive 1` | Archive a habit |
| **Reminders** | |
| `!remind me 30m Take a break` | Reminder in 30 minutes |
| `!remind me 2h Check oven` | Reminder in 2 hours |
| `!remind me 1d Stand up meeting` | Reminder in 1 day |
| `!remind me 2025-03-10 15:00 Call doctor` | Reminder at a specific date and time |
| `!remind list` | List pending reminders |
| `!remind cancel 2` | Cancel reminder #2 |
| **AI Assistant** | |
| `!ask <question>` | Ask Boiler anything — remembers the conversation per channel |
| `!memory clear` | Wipe Boiler's memory for the current channel |
| `!memory status` | See how many messages Boiler currently remembers |
| **End of Day** | |
| `!eod` | Trigger your end-of-day summary early _(owner only)_ |

## AI Conversation Memory

Boiler remembers the last N messages (configurable via `Ollama:ContextMessages`) of each conversation, scoped per user per channel. This means:

- Conversations in **different channels are fully isolated** - Boiler won't mix up context between your server and DMs
- History is **in-memory only** - it resets when the bot restarts, keeping things simple and private
- The embed footer shows how many messages are currently in context
- Use `!memory clear` to start a fresh conversation at any time without restarting the bot

## End-of-Day Summary

At **9pm in your configured timezone**, Boiler will automatically DM you an AI-generated summary of your day covering:

- ✅ Tasks completed today
- ➕ Tasks added today
- ⬜ Tasks still pending (with overdue warnings)
- ✅ Habits logged and current streaks
- ❌ Habits missed

You can also call `!eod` at any time to trigger it early. Only one summary is sent per day - if you use `!eod`, the automatic 9pm trigger is skipped. The daily flag resets at midnight in your configured timezone.

EOD requires Ollama to be active. If no model was selected at startup, `!eod` is disabled for that session.

## Database & Migrations

Schema changes are managed with EF Core migrations. The bot calls `MigrateAsync()` on startup, so any pending migrations are applied automatically - you never need to touch the database manually.

### Adding a schema change

After modifying a model in `Models.cs` or `BotDbContext.cs`:

```bash
dotnet ef migrations add YourMigrationName
```

The next time the bot starts, the migration is applied automatically.

### Upgrading an existing database (pre-migrations)

If you were running the bot before migrations were introduced, your database has the correct schema but no `__EFMigrationsHistory` table. Mark the baseline migration as already applied without re-running it:

```bash
dotnet ef database update InitialCreate --connection "Data Source=data/productivity.db"
```

Then start the bot normally. Future migrations will apply as usual.

## Project Structure

```
Boiler-bot/
├── config/
│   ├── appsettings.json          # Bot config (committed, no secrets)
│   └── appsettings.local.json    # Your real token (gitignored)
├── Commands/
│   ├── GeneralCommands.cs        # !help, !ping
│   ├── TaskCommands.cs           # !task *
│   ├── HabitCommands.cs          # !habit *
│   ├── ReminderCommands.cs       # !remind *
│   ├── AskCommands.cs            # !ask
│   ├── MemoryCommands.cs         # !memory *
│   └── EodCommands.cs            # !eod
├── Services/
│   ├── BotHostedService.cs       # Bot startup/shutdown + model selection
│   ├── CommandHandlerService.cs  # Routes messages to commands
│   ├── TaskService.cs            # Task business logic
│   ├── HabitService.cs           # Habit + streak logic
│   ├── ReminderService.cs        # Background reminder timer
│   ├── OllamaService.cs          # Ollama HTTP client + conversation history
│   └── EodService.cs             # End-of-day summary logic + timer
├── Models/
│   └── Models.cs                 # EF Core entity models
├── Data/
│   └── BotDbContext.cs           # SQLite database context
├── Migrations/                   # EF Core migration files (auto-generated)
└── ProductivityBot.csproj
```

## Next Steps / Ideas

- [ ] Slash commands (Discord's modern `/command` style)
- [ ] `!task edit <id>` command
- [ ] Morning briefing DM at a configured time
- [ ] Notes/journal module
- [ ] Per-server vs per-user data separation