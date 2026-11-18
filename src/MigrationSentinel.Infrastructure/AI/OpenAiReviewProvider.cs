using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Infrastructure.AI;

/// <summary>
/// OpenAI-compatible Chat Completions client (works with OpenAI API, Codex models, Azure OpenAI, OpenRouter, vLLM).
/// </summary>
public sealed class OpenAiReviewProvider : IAiReviewProvider
{
    public const string DefaultEndpoint = "https://api.openai.com/v1/chat/completions";
    public const string DefaultModel = "gpt-4o-mini";

    private readonly HttpClient _httpClient;
    private readonly AiProviderOptions _options;

    public OpenAiReviewProvider(AiProviderOptions options, HttpClient? httpClient = null)
    {
        _options = options;
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(Math.Max(5, options.TimeoutSeconds)) };
    }

    public string ProviderName => "OpenAI";

    public async Task<AiReviewResult> ReviewAsync(AnalysisReport deterministicReport, CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(_options.Model) ? DefaultModel : _options.Model;
        var endpoint = string.IsNullOrWhiteSpace(_options.Endpoint) ? DefaultEndpoint : _options.Endpoint;
        var apiKey = !string.IsNullOrWhiteSpace(_options.ApiKey)
            ? _options.ApiKey
            : Environment.GetEnvironmentVariable("OPENAI_API_KEY");

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new AiReviewResult
            {
                Provider = ProviderName,
                Model = model,
                ExecutiveSummary = "AI review skipped because OPENAI_API_KEY is not configured.",
                Succeeded = false,
                ErrorMessage = "Missing OpenAI API key. Set OPENAI_API_KEY or pass --ai-key."
            };
        }

        try
        {
            var payload = new
            {
                model,
                temperature = 0.2,
                response_format = new { type = "json_object" },
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
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return new AiReviewResult
                {
                    Provider = ProviderName,
                    Model = model,
                    ExecutiveSummary = "OpenAI API returned an error.",
                    Succeeded = false,
                    ErrorMessage = $"HTTP {(int)response.StatusCode}: {body}"
                };
            }

            using var json = JsonDocument.Parse(body);
            var content = json.RootElement
                .GetProperty("choices")[0]
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
                ExecutiveSummary = "AI review encountered an error; deterministic AST findings remain authoritative.",
                Succeeded = false,
                ErrorMessage = ex.Message
            };
        }
    }
}
