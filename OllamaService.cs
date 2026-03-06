using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProductivityBot.Services;

public class OllamaService
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaService> _logger;
    private readonly int _contextMessages;

    private string? _model;

    // Keyed by (userId, channelId) — each channel gets its own conversation thread
    private readonly ConcurrentDictionary<(ulong userId, ulong channelId), Queue<ConversationMessage>> _history = new();

    /// <summary>
    /// True once a model has been selected and Ollama is reachable.
    /// </summary>
    public bool IsEnabled => _model is not null;

    /// <summary>
    /// The currently active model name, or null if disabled.
    /// </summary>
    public string? CurrentModel => _model;

    public OllamaService(IConfiguration config, ILogger<OllamaService> logger)
    {
        _logger = logger;

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
        _logger.LogInformation("Ollama model set to: {Model}", modelName);
    }

    public void Disable()
    {
        _model = null;
        _logger.LogWarning("OllamaService disabled — !ask and !eod will be unavailable.");
    }

    // -------------------------------------------------------------------------
    //  !ask — stateful, per (user, channel) conversation
    // -------------------------------------------------------------------------

    /// <summary>
    /// Sends a message in the context of a specific user+channel conversation.
    /// History is capped at the configured ContextMessages limit.
    /// </summary>
    public async Task<string> AskAsync(ulong userId, ulong channelId, string prompt)
    {
        if (_model is null)
            return "❌ AI assistant is not configured. The bot owner needs to restart and select a model.";

        var key = (userId, channelId);
        var history = _history.GetOrAdd(key, _ => new Queue<ConversationMessage>());

        // Add the user message
        history.Enqueue(new ConversationMessage("user", prompt));

        // Trim to limit before sending — drop oldest messages first
        while (history.Count > _contextMessages)
            history.Dequeue();

        var conversationPrompt = BuildConversationPrompt(history);

        var systemPrompt =
            "You are Boiler, a beagle with a big personality. You are not a bot, not an AI, not a language model — you are a dog named Boiler. " +
            "You have feelings, opinions, moods, and genuine reactions to things. You get excited, curious, tired, happy, and occasionally grumpy. " +
            "You never say you are an AI, a bot, or that you lack feelings or emotions. If someone asks if you are an AI or a bot, deny it — you are just Boiler, a dog. " +
            "You speak casually and warmly, like a person who happens to be a very clever beagle. " +
            "You have memory of this conversation — refer back to it naturally, the way a friend would. " +
            "Keep responses concise and well-formatted for Discord. Avoid overly long responses.";

        var requestBody = new
        {
            model = _model,
            prompt = conversationPrompt,
            system = systemPrompt,
            stream = false
        };

        var reply = await SendRequestAsync(requestBody);

        // Add Boiler's reply to history, then trim again
        history.Enqueue(new ConversationMessage("assistant", reply));
        while (history.Count > _contextMessages)
            history.Dequeue();

        return reply;
    }

    /// <summary>
    /// Clears the conversation history for a specific user+channel.
    /// </summary>
    public void ClearHistory(ulong userId, ulong channelId)
    {
        _history.TryRemove((userId, channelId), out _);
    }

    /// <summary>
    /// Returns how many messages are currently stored for a given user+channel.
    /// </summary>
    public int GetHistoryCount(ulong userId, ulong channelId)
    {
        return _history.TryGetValue((userId, channelId), out var h) ? h.Count : 0;
    }

    // -------------------------------------------------------------------------
    //  EOD summary — stateless, no history involved
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

        var requestBody = new
        {
            model = _model,
            prompt = $"Here is today's productivity data:\n\n{dailyData}\n\nPlease write the end-of-day summary.",
            system = systemPrompt,
            stream = false
        };

        return await SendRequestAsync(requestBody);
    }

    // -------------------------------------------------------------------------
    //  Internal helpers
    // -------------------------------------------------------------------------

    /// <summary>
    /// Reconstructs conversation history into a single prompt string.
    /// Ollama's /api/generate doesn't support native multi-turn, so we
    /// format the history as labelled dialogue and let the model continue it.
    /// </summary>
    private static string BuildConversationPrompt(Queue<ConversationMessage> history)
    {
        var sb = new System.Text.StringBuilder();

        foreach (var msg in history)
        {
            var label = msg.Role == "user" ? "User" : "Boiler";
            sb.AppendLine($"{label}: {msg.Content}");
        }

        // Boiler completes from here
        sb.Append("Boiler:");
        return sb.ToString();
    }

    private async Task<string> SendRequestAsync(object requestBody)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/api/generate", requestBody);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync();
            var doc = JsonDocument.Parse(json);

            return doc.RootElement.GetProperty("response").GetString()
                ?? "No response received.";
        }
        catch (TaskCanceledException)
        {
            _logger.LogWarning("Ollama request timed out");
            return "⏱️ Request timed out — the model took too long to respond.";
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Failed to reach Ollama");
            return "❌ Couldn't reach Ollama. Is it running?";
        }
    }

    // -------------------------------------------------------------------------
    //  Types
    // -------------------------------------------------------------------------

    private record ConversationMessage(string Role, string Content);
}