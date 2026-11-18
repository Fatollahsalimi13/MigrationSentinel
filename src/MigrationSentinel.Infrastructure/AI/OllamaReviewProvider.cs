using System.Text;
using System.Text.Json;
using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Infrastructure.AI;

/// <summary>
/// Privacy-first local Ollama LLM client for air-gapped or enterprise self-hosted migration reviews.
/// </summary>
public sealed class OllamaReviewProvider : IAiReviewProvider
{
    public const string DefaultBaseUrl = "http://localhost:11434";
    public const string DefaultModel = "qwen2.5-coder:7b";

    private readonly HttpClient _httpClient;
    private readonly AiProviderOptions _options;

    public OllamaReviewProvider(AiProviderOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(10, options.TimeoutSeconds)) };
    }

    public string ProviderName => "Ollama";

    public async Task<AiReviewResult> ReviewAsync(AnalysisReport deterministicReport, CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(_options.Model) ? DefaultModel : _options.Model;
        var baseUrl = !string.IsNullOrWhiteSpace(_options.Endpoint)
            ? _options.Endpoint
            : Environment.GetEnvironmentVariable("OLLAMA_HOST") ?? DefaultBaseUrl;

        var endpoint = baseUrl.EndsWith("/api/chat", StringComparison.OrdinalIgnoreCase)
            ? baseUrl
            : $"{baseUrl.TrimEnd('/')}/api/chat";

        try
        {
            var payload = new
            {
                model,
                stream = false,
                format = "json",
                messages = new object[]
                {
                    new { role = "system", content = AiPromptBuilder.SystemPrompt },
                    new { role = "user", content = AiPromptBuilder.BuildUserPrompt(deterministicReport) }
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new AiReviewResult
                {
                    Provider = ProviderName,
                    Model = model,
                    ExecutiveSummary = "Ollama endpoint returned an error.",
                    Succeeded = false,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}: {body}"
                };
            }

            using var json = JsonDocument.Parse(body);
            var content = json.RootElement
                .GetProperty("message")
                .GetProperty("content")
                .GetString() ?? string.Empty;

            return AiPromptBuilder.ParseStructuredResponse(ProviderName, model, content);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AiReviewResult
            {
                Provider = ProviderName,
                Model = model,
                ExecutiveSummary = "Could not reach local Ollama server; deterministic AST findings remain authoritative.",
                Succeeded = false,
                ErrorMessage = ex.Message
            };
        }
    }
}

public static class AiReviewProviderFactory
{
    public static IAiReviewProvider? Create(AiProviderOptions options, HttpClient? httpClient = null)
    {
        var provider = options.Provider?.Trim().ToLowerInvariant();
        return provider switch
        {
            "openai" => new OpenAiReviewProvider(options, httpClient),
            "ollama" => new OllamaReviewProvider(options, httpClient),
            _ => null
        };
    }
}
