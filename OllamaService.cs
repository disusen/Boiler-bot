using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ProductivityBot.Services;

public class OllamaService
{
	private readonly HttpClient _http;
	private readonly string _model;
	private readonly ILogger<OllamaService> _logger;

	public OllamaService(IConfiguration config, ILogger<OllamaService> logger)
	{
		_logger = logger;
		_model = config["Ollama:Model"] ?? "phi4";
		var baseUrl = config["Ollama:BaseUrl"] ?? "http://localhost:11434";

		_http = new HttpClient
		{
			BaseAddress = new Uri(baseUrl),
			Timeout = TimeSpan.FromSeconds(120)
		};
	}

	public async Task<string> AskAsync(string prompt, string? systemPrompt = null)
	{
		var requestBody = new
		{
			model = _model,
			prompt = prompt,
			system = systemPrompt ?? "You are Boiler, a helpful assistant living inside a Discord bot. Keep responses concise and well-formatted for Discord. Avoid overly long responses.",
			stream = false
		};

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