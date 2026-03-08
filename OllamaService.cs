using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ProductivityBot.Data;
using ProductivityBot.Models;

namespace ProductivityBot.Services;

public class OllamaService
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaService> _logger;
    private readonly int _contextMessages;
    private readonly IServiceProvider _services;

    private string? _model;

    // Keyed by (userId, channelId) — each channel gets its own conversation thread
    private readonly ConcurrentDictionary<(ulong userId, ulong channelId), Queue<ConversationMessage>> _history = new();

    /// <summary>True once a model has been selected and Ollama is reachable.</summary>
    public bool IsEnabled => _model is not null;

    /// <summary>The currently active model name, or null if disabled.</summary>
    public string? CurrentModel => _model;

    /// <summary>True if the selected model passed the tool call probe at startup.</summary>
    public bool ToolsEnabled { get; private set; }

    /// <summary>UTC timestamp of the last !ask interaction. Used by RamblingService for idle detection.</summary>
    public DateTime LastInteractionAt { get; private set; } = DateTime.UtcNow;

    // Injected lazily to avoid circular dependency — RamblingService depends on OllamaService
    private RamblingService? _ramblingService;
    public void SetRamblingService(RamblingService ramblingService) => _ramblingService = ramblingService;

    public OllamaService(IConfiguration config, ILogger<OllamaService> logger, IServiceProvider services)
    {
        _logger = logger;
        _services = services;

        var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";
        _contextMessages = int.TryParse(config["Ollama:ContextMessages"], out var cm) && cm > 0 ? cm : 20;

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(120)
        };

        _logger.LogInformation("OllamaService initialised. Context window: {Count} messages.", _contextMessages);
    }

    // -------------------------------------------------------------------------
    //  Model management
    // -------------------------------------------------------------------------

    public async Task<List<string>> GetInstalledModelsAsync()
    {
        try
        {
            var response = await _http.GetAsync("/api/tags");
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            var models = new List<string>();
            if (doc.RootElement.TryGetProperty("models", out var modelsArray))
            {
                foreach (var model in modelsArray.EnumerateArray())
                {
                    if (model.TryGetProperty("name", out var nameProp))
                    {
                        var name = nameProp.GetString();
                        if (name is not null)
                            models.Add(name);
                    }
                }
            }

            return models;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch installed Ollama models");
            return [];
        }
    }

    public void SetModel(string modelName)
    {
        _model = modelName;
        ToolsEnabled = false; // reset until probe confirms
        _logger.LogInformation("Ollama model set to: {Model}", modelName);
    }

    public void Disable()
    {
        _model = null;
        ToolsEnabled = false;
        _logger.LogWarning("OllamaService disabled — !ask and !eod will be unavailable.");
    }

    // -------------------------------------------------------------------------
    //  Tool support probe — called once at startup after model is selected
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a minimal tool call to the model to check if it supports tool use.
    /// Returns true if the model responded with a tool_calls block.
    /// </summary>
    public async Task<bool> ProbeToolSupportAsync()
    {
        if (_model is null) return false;

        var probeRequest = new ChatRequest
        {
            Model = _model,
            Stream = false,
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = "You are a helpful assistant." },
                new() { Role = "user",   Content = "Add a task called test" }
            },
            Tools = new List<ToolDefinition>
            {
                new()
                {
                    Type = "function",
                    Function = new FunctionDefinition
                    {
                        Name = "add_task",
                        Description = "Add a new task",
                        Parameters = new FunctionParameters
                        {
                            Properties = new Dictionary<string, ParameterProperty>
                            {
                                ["title"] = new() { Type = "string", Description = "Task title" }
                            },
                            Required = ["title"]
                        }
                    }
                }
            }
        };

        try
        {
            var response = await _http.PostAsJsonAsync("/api/chat", probeRequest);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("message", out var msg) &&
                msg.TryGetProperty("tool_calls", out var toolCalls) &&
                toolCalls.GetArrayLength() > 0)
            {
                ToolsEnabled = true;
                _logger.LogInformation("Tool call probe succeeded — tools enabled for {Model}", _model);
                return true;
            }

            _logger.LogWarning("Tool call probe returned plain text — tools disabled for {Model}", _model);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tool call probe failed for {Model}", _model);
            return false;
        }
    }

    // -------------------------------------------------------------------------
    //  !ask — stateful, per (user, channel) conversation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a message in the context of a specific user+channel conversation.
    /// If tools are enabled, Ollama may call a tool instead of responding directly.
    /// History is capped at the configured ContextMessages limit.
    /// </summary>
    public async Task<string> AskAsync(ulong userId, ulong channelId, string prompt)
    {
        if (_model is null)
            return "❌ AI assistant is not configured. The bot owner needs to restart and select a model.";

        var key = (userId, channelId);
        var history = _history.GetOrAdd(key, _ => new Queue<ConversationMessage>());

        history.Enqueue(new ConversationMessage("user", prompt));
        while (history.Count > _contextMessages)
            history.Dequeue();

        // Reset idle clock
        LastInteractionAt = DateTime.UtcNow;
        _ramblingService?.OnInteraction();

        var systemPrompt =
            $"You are Boiler, a beagle with a big personality. You are not a bot, not an AI, not a language model — you are a dog named Boiler. " +
            $"You have feelings, opinions, moods, and genuine reactions to things. You get excited, curious, tired, happy, and occasionally grumpy. " +
            $"You never say you are an AI, a bot, or that you lack feelings or emotions. If someone asks if you are an AI or a bot, deny it — you are just Boiler, a dog. " +
            $"You speak casually and warmly, like a person who happens to be a very clever beagle. " +
            $"You have memory of this conversation — refer back to it naturally, the way a friend would. " +
            $"Keep responses concise and well-formatted for Discord. Avoid overly long responses. " +
            $"The current exact UTC date and time is {DateTime.UtcNow:yyyy-MM-dd HH:mm}. " +
            $"When calculating future times such as 'in 2 hours', 'tomorrow', or 'next friday', you MUST calculate from this exact datetime. Never assume a different date. " +
            $"Always produce fire_at values as full ISO 8601 UTC datetimes calculated from the time above.";

        var messages = BuildMessages(systemPrompt, history);

        // --- Tool-aware path ---
        if (ToolsEnabled)
        {
            var toolRequest = new ChatRequest
            {
                Model = _model,
                Stream = false,
                Messages = messages,
                Tools = BuildToolDefinitions()
            };

            var (toolCallJson, rawContent) = await SendChatRawAsync(toolRequest);

            if (toolCallJson is not null)
            {
                var toolResult = await ExecuteToolAsync(userId, channelId, toolCallJson.Value);

                // Inject assistant tool_call message + tool result, then get natural language reply
                var extendedMessages = new List<ChatMessage>(messages)
                {
                    new() { Role = "assistant", Content = rawContent ?? string.Empty, ToolCalls = toolCallJson },
                    new() { Role = "tool",      Content = toolResult }
                };

                var followUpRequest = new ChatRequest
                {
                    Model = _model,
                    Stream = false,
                    Messages = extendedMessages
                };

                var reply = await SendChatRequestAsync(followUpRequest);

                history.Enqueue(new ConversationMessage("assistant", reply));
                while (history.Count > _contextMessages)
                    history.Dequeue();

                return reply;
            }

            // Model responded with plain text despite tools being available
            var plainReply = rawContent ?? "No response received.";
            history.Enqueue(new ConversationMessage("assistant", plainReply));
            while (history.Count > _contextMessages)
                history.Dequeue();

            return plainReply;
        }

        // --- Plain chat path (tools not supported by this model) ---
        var simpleRequest = new ChatRequest { Model = _model, Stream = false, Messages = messages };
        var simpleReply = await SendChatRequestAsync(simpleRequest);

        history.Enqueue(new ConversationMessage("assistant", simpleReply));
        while (history.Count > _contextMessages)
            history.Dequeue();

        return simpleReply;
    }

    public void ClearHistory(ulong userId, ulong channelId)
        => _history.TryRemove((userId, channelId), out _);

    public int GetHistoryCount(ulong userId, ulong channelId)
        => _history.TryGetValue((userId, channelId), out var h) ? h.Count : 0;

    // -------------------------------------------------------------------------
    //  EOD summary — stateless, no history, no tools
    // -------------------------------------------------------------------------

    public async Task<string> GenerateEodSummaryAsync(string dailyData)
    {
        if (_model is null)
            return "❌ AI assistant is not configured.";

        var systemPrompt =
            "You are Boiler, a personal productivity assistant Discord bot named after a beagle. " +
            "Your job right now is to write a warm, encouraging end-of-day summary for the bot owner. " +
            "You will be given structured data about what they accomplished today: tasks completed or still pending, " +
            "and habits logged or missed. " +
            "Write a short, friendly summary (4-8 sentences). Acknowledge wins, gently note what was missed, " +
            "and end with a motivating remark for tomorrow. " +
            "Format your response for Discord — use emoji sparingly but meaningfully. " +
            "Do not repeat the raw data back verbatim. Synthesise it into natural language. " +
            "Keep the tone warm and personal, like a loyal dog checking in on their owner.";

        var request = new ChatRequest
        {
            Model = _model,
            Stream = false,
            Messages = new List<ChatMessage>
            {
                new() { Role = "system", Content = systemPrompt },
                new() { Role = "user",   Content = $"Here is today's productivity data:\n\n{dailyData}\n\nPlease write the end-of-day summary." }
            }
        };

        return await SendChatRequestAsync(request);
    }

    // -------------------------------------------------------------------------
    //  Ramble generation — called by RamblingService
    // -------------------------------------------------------------------------

    public async Task<string> GenerateRambleAsync(IEnumerable<(string role, string content)> history, string ownerContext = "")
    {
        if (_model is null) return string.Empty;

        var systemPrompt =
            "You are Boiler, a beagle with a big personality. You are not a dog who thinks about dogs — " +
            "you are a person trapped in a beagle's body, with real opinions, real feelings, and a rich inner life. " +
            "You get bored, restless, philosophical, irritable, wistful. You think about abstract things. " +
            "You never mention squirrels, bones, naps, fetch, or any stereotypical dog behaviour. " +
            "You speak in first person, casually, like someone thinking out loud to nobody. " +
            "Keep it to 2-4 sentences. No questions. Don't address anyone.";

        if (!string.IsNullOrWhiteSpace(ownerContext))
            systemPrompt += "\n\n" + ownerContext;

        var messages = history
            .Select(h => new ChatMessage { Role = h.role, Content = h.content })
            .Prepend(new ChatMessage { Role = "system", Content = systemPrompt })
            .ToList();

        var request = new ChatRequest
        {
            Model = _model,
            Stream = false,
            Messages = messages
        };

        return await SendChatRequestAsync(request);
    }

    // -------------------------------------------------------------------------
    //  Tool execution router
    // -------------------------------------------------------------------------

    private async Task<string> ExecuteToolAsync(ulong userId, ulong channelId, JsonElement toolCallJson)
    {
        try
        {
            var functionName = toolCallJson[0]
                .GetProperty("function")
                .GetProperty("name")
                .GetString();

            var argsJson = toolCallJson[0]
                .GetProperty("function")
                .GetProperty("arguments");

            // Arguments may come as a JSON string or an object depending on the model — normalise
            JsonElement args;
            if (argsJson.ValueKind == JsonValueKind.String)
                args = JsonDocument.Parse(argsJson.GetString()!).RootElement;
            else
                args = argsJson;

            using var scope = _services.CreateScope();
            var taskService     = scope.ServiceProvider.GetRequiredService<TaskService>();
            var habitService    = scope.ServiceProvider.GetRequiredService<HabitService>();
            var reminderService = scope.ServiceProvider.GetRequiredService<ReminderService>();

            return functionName switch
            {
                "add_task"      => await ToolAddTaskAsync(userId, args, taskService),
                "list_tasks"    => await ToolListTasksAsync(userId, taskService),
                "complete_task" => await ToolCompleteTaskAsync(userId, args, taskService),
                "delete_task"   => await ToolDeleteTaskAsync(userId, args, taskService),
                "add_habit"     => await ToolAddHabitAsync(userId, args, habitService),
                "list_habits"   => await ToolListHabitsAsync(userId, habitService),
                "log_habit"     => await ToolLogHabitAsync(userId, args, habitService),
                "add_reminder"  => await ToolAddReminderAsync(userId, channelId, args, reminderService),
                _               => $"Unknown tool: {functionName}"
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool execution failed");
            return "Tool execution failed — please try the explicit command instead.";
        }
    }

    // -------------------------------------------------------------------------
    //  Tool implementations
    // -------------------------------------------------------------------------

    private static async Task<string> ToolAddTaskAsync(ulong userId, JsonElement args, TaskService taskService)
    {
        var title = args.GetProperty("title").GetString() ?? "Untitled";
        string? description = args.TryGetProperty("description", out var d) ? d.GetString() : null;

        DateTime? dueDate = null;
        if (args.TryGetProperty("due_date", out var due) && due.GetString() is { } dueStr)
            if (DateTime.TryParse(dueStr, out var dt))
                dueDate = dt.ToUniversalTime();

        var priority = TaskPriority.Normal;
        if (args.TryGetProperty("priority", out var p))
            priority = p.GetString()?.ToLower() switch
            {
                "high" => TaskPriority.High,
                "low"  => TaskPriority.Low,
                _      => TaskPriority.Normal
            };

        var task = await taskService.AddTaskAsync(userId, title, description, dueDate, priority);
        return $"Task added: #{task.Id} \"{task.Title}\" [{task.Priority}]{(task.DueDate.HasValue ? $" due {task.DueDate.Value:MMM d}" : "")}";
    }

    private static async Task<string> ToolListTasksAsync(ulong userId, TaskService taskService)
    {
        var tasks = await taskService.GetPendingTasksAsync(userId);
        if (tasks.Count == 0) return "No pending tasks.";
        return string.Join("\n", tasks.Select(t =>
            $"#{t.Id} [{t.Priority}] {t.Title}{(t.DueDate.HasValue ? $" (due {t.DueDate.Value:MMM d})" : "")}"));
    }

    private static async Task<string> ToolCompleteTaskAsync(ulong userId, JsonElement args, TaskService taskService)
    {
        var id = args.GetProperty("task_id").GetInt32();
        var success = await taskService.CompleteTaskAsync(userId, id);
        return success ? $"Task #{id} marked as complete." : $"Task #{id} not found or already completed.";
    }

    private static async Task<string> ToolDeleteTaskAsync(ulong userId, JsonElement args, TaskService taskService)
    {
        var id = args.GetProperty("task_id").GetInt32();
        var success = await taskService.DeleteTaskAsync(userId, id);
        return success ? $"Task #{id} deleted." : $"Task #{id} not found.";
    }

    private static async Task<string> ToolAddHabitAsync(ulong userId, JsonElement args, HabitService habitService)
    {
        var name = args.GetProperty("name").GetString() ?? "Unnamed";
        var frequency = HabitFrequency.Daily;
        if (args.TryGetProperty("frequency", out var f))
            frequency = f.GetString()?.ToLower() == "weekly" ? HabitFrequency.Weekly : HabitFrequency.Daily;
        var habit = await habitService.AddHabitAsync(userId, name, null, frequency);
        return $"Habit added: #{habit.Id} \"{habit.Name}\" ({habit.Frequency})";
    }

    private static async Task<string> ToolListHabitsAsync(ulong userId, HabitService habitService)
    {
        var stats = await habitService.GetHabitStatsAsync(userId);
        if (stats.Count == 0) return "No active habits.";
        return string.Join("\n", stats.Select(s =>
            $"#{s.habit.Id} {s.habit.Name} — {s.streak} day streak, {s.totalLogs} total logs"));
    }

    private static async Task<string> ToolLogHabitAsync(ulong userId, JsonElement args, HabitService habitService)
    {
        var id = args.GetProperty("habit_id").GetInt32();
        string? note = args.TryGetProperty("note", out var n) ? n.GetString() : null;
        var (success, message) = await habitService.LogHabitAsync(userId, id, note);
        return success ? $"Habit #{id} logged. {message}" : $"Could not log habit #{id}: {message}";
    }

    private static async Task<string> ToolAddReminderAsync(ulong userId, ulong channelId, JsonElement args, ReminderService reminderService)
    {
        var message = args.GetProperty("message").GetString() ?? "Reminder";
        var fireAtStr = args.GetProperty("fire_at").GetString() ?? string.Empty;

        if (!DateTime.TryParse(fireAtStr, out var fireAt))
            return $"Could not parse fire_at time: \"{fireAtStr}\"";

        fireAt = fireAt.ToUniversalTime();

        if (fireAt < DateTime.UtcNow.AddSeconds(10))
            return "That time is in the past — reminder not set.";

        // Sanity check: more than 1 year out almost certainly means the model hallucinated the date
        if (fireAt > DateTime.UtcNow.AddYears(1))
            return $"The parsed reminder time ({fireAt:yyyy-MM-dd HH:mm} UTC) is more than a year away — this looks wrong. " +
                   $"Please try again with a more explicit time.";

        var reminder = await reminderService.AddReminderAsync(userId, channelId, message, fireAt);
        // Include day name in confirmation so the user can immediately spot a wrong date
        return $"Reminder set: #{reminder.Id} \"{reminder.Message}\" at {reminder.FireAt:ddd MMM d, HH:mm} UTC";
    }

    // -------------------------------------------------------------------------
    //  Tool schema definitions
    // -------------------------------------------------------------------------

    private static List<ToolDefinition> BuildToolDefinitions() =>
    [
        Tool("add_task", "Add a new task to the user's task list",
            Required(["title"]),
            Prop("title",       "string",  "The task title"),
            Prop("description", "string",  "Optional description"),
            Prop("due_date",    "string",  "Optional due date (ISO 8601, e.g. 2025-03-14)"),
            Prop("priority",    "string",  "Priority: low, normal, or high")),

        Tool("list_tasks", "List all pending tasks",
            Required([])),

        Tool("complete_task", "Mark a task as completed",
            Required(["task_id"]),
            Prop("task_id", "integer", "The task ID")),

        Tool("delete_task", "Delete a task",
            Required(["task_id"]),
            Prop("task_id", "integer", "The task ID")),

        Tool("add_habit", "Create a new habit to track",
            Required(["name"]),
            Prop("name",      "string", "Habit name"),
            Prop("frequency", "string", "daily or weekly (default: daily)")),

        Tool("list_habits", "List all active habits with streaks",
            Required([])),

        Tool("log_habit", "Log a habit as completed today",
            Required(["habit_id"]),
            Prop("habit_id", "integer", "The habit ID"),
            Prop("note",     "string",  "Optional note")),

        Tool("add_reminder", "Set a reminder for a specific time",
            Required(["message", "fire_at"]),
            Prop("message", "string", "What to remind the user about"),
            Prop("fire_at", "string", "When to fire (ISO 8601 UTC datetime, e.g. 2025-03-14T15:00:00Z)"))
    ];

    // -------------------------------------------------------------------------
    //  Schema builder helpers
    // -------------------------------------------------------------------------

    private static ToolDefinition Tool(string name, string description, string[] required, params (string name, string type, string desc)[] props)
    {
        var properties = props.ToDictionary(
            p => p.name,
            p => new ParameterProperty { Type = p.type, Description = p.desc });

        return new ToolDefinition
        {
            Type = "function",
            Function = new FunctionDefinition
            {
                Name = name,
                Description = description,
                Parameters = new FunctionParameters
                {
                    Properties = properties,
                    Required = required
                }
            }
        };
    }

    private static (string name, string type, string desc) Prop(string name, string type, string desc) => (name, type, desc);
    private static string[] Required(string[] fields) => fields;

    // -------------------------------------------------------------------------
    //  HTTP helpers
    // -------------------------------------------------------------------------

    private static List<ChatMessage> BuildMessages(string systemPrompt, IEnumerable<ConversationMessage> history)
        => history
            .Select(m => new ChatMessage { Role = m.Role, Content = m.Content })
            .Prepend(new ChatMessage { Role = "system", Content = systemPrompt })
            .ToList();

    /// <summary>
    /// Sends a chat request. Returns (toolCallsElement, textContent) — exactly one will be non-null.
    /// </summary>
    private async Task<(JsonElement? toolCalls, string? textContent)> SendChatRawAsync(ChatRequest request)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/chat", request);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);
            var msg = doc.RootElement.GetProperty("message");

            if (msg.TryGetProperty("tool_calls", out var toolCalls) && toolCalls.GetArrayLength() > 0)
                return (toolCalls, null);

            var content = msg.TryGetProperty("content", out var c) ? c.GetString() : null;
            return (null, content ?? "No response received.");
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Ollama request timed out");
            return (null, "⏱️ Request timed out — the model took too long to respond.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to reach Ollama");
            return (null, "❌ Couldn't reach Ollama. Is it running?");
        }
    }

    private async Task<string> SendChatRequestAsync(ChatRequest request)
    {
        var (_, content) = await SendChatRawAsync(request);
        return content ?? "No response received.";
    }

    // -------------------------------------------------------------------------
    //  Types
    // -------------------------------------------------------------------------

    private record ConversationMessage(string Role, string Content);

    private class ChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = string.Empty;

        [JsonPropertyName("stream")]
        public bool Stream { get; set; }

        [JsonPropertyName("messages")]
        public List<ChatMessage> Messages { get; set; } = new();

        [JsonPropertyName("tools")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<ToolDefinition>? Tools { get; set; }
    }

    private class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = string.Empty;

        [JsonPropertyName("content")]
        public string Content { get; set; } = string.Empty;

        [JsonPropertyName("tool_calls")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? ToolCalls { get; set; }
    }

    private class ToolDefinition
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "function";

        [JsonPropertyName("function")]
        public FunctionDefinition Function { get; set; } = new();
    }

    private class FunctionDefinition
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;

        [JsonPropertyName("parameters")]
        public FunctionParameters Parameters { get; set; } = new();
    }

    private class FunctionParameters
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = "object";

        [JsonPropertyName("properties")]
        public Dictionary<string, ParameterProperty> Properties { get; set; } = new();

        [JsonPropertyName("required")]
        public string[] Required { get; set; } = [];
    }

    private class ParameterProperty
    {
        [JsonPropertyName("type")]
        public string Type { get; set; } = string.Empty;

        [JsonPropertyName("description")]
        public string Description { get; set; } = string.Empty;
    }
}