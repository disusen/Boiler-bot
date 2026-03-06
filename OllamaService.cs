using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProductivityBot.Services;

public class OllamaService
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaService> _logger;

    private string? _model;

    /// <summary>
    /// True once a model has been selected and Ollama is reachable.
    /// AskCommands and EodService should check this before processing requests.
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

        _http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl),
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    /// <summary>
    /// Queries Ollama for all locally installed models.
    /// Returns an empty list if Ollama is unreachable.
    /// </summary>
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

    /// <summary>
    /// Sets the active model. Call this after the owner selects one at startup.
    /// </summary>
    public void SetModel(string modelName)
    {
        _model = modelName;
        _logger.LogInformation("Ollama model set to: {Model}", modelName);
    }

    /// <summary>
    /// Disables the AI feature entirely (no model selected or Ollama unavailable).
    /// </summary>
    public void Disable()
    {
        _model = null;
        _logger.LogWarning("OllamaService disabled — !ask and !eod will be unavailable.");
    }

    /// <summary>
    /// General-purpose prompt for !ask.
    /// </summary>
    public async Task<string> AskAsync(string prompt, string? systemPrompt = null)
    {
        if (_model is null)
            return "❌ AI assistant is not configured. The bot owner needs to restart and select a model.";

        var requestBody = new
        {
            model = _model,
            prompt = prompt,
            system = systemPrompt ?? "You are Boiler, a Discord bot. You were named after a beagle called Boiler. If anyone asks who or what you are, you are Boiler — not an AI model, just Boiler. Keep responses concise and well-formatted for Discord. Avoid overly long responses.",
            stream = false
        };

        return await SendRequestAsync(requestBody);
    }

    /// <summary>
    /// Generates a personalised end-of-day summary from structured daily data.
    /// The data string should contain pre-formatted task and habit information.
    /// </summary>
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

    // --- Internal ---

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
}
