using System.Text;
using System.Text.Json;
using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Infrastructure.AI;

public sealed record AiProviderOptions
{
    public string Provider { get; init; } = "none";
    public string? ApiKey { get; init; }
    public string? Endpoint { get; init; }
    public string? Model { get; init; }
    public int TimeoutSeconds { get; init; } = 60;
}

internal static class AiPromptBuilder
{
    public const string SystemPrompt = """
        You are a Principal Database Reliability Engineer (DBRE) and Entity Framework Core architect specializing in zero-downtime migrations for SQL Server and PostgreSQL.
        Review the deterministic AST risk findings and migration excerpts provided.
        Respond strictly with a single valid JSON object (no markdown fences) matching this exact schema:
        {
          "executiveSummary": "2-3 sentence executive risk summary for engineering & DBA sign-off.",
          "additionalRisks": ["Any subtle cross-statement, locking, or performance risks beyond the deterministic findings."],
          "rolloutChecklist": ["Concrete step-by-step zero-downtime deployment checklist items."],
          "suggestedMitigationSql": "-- Optional consolidated safe SQL rewrite or guard script"
        }
        """;

    public static string BuildUserPrompt(AnalysisReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Target: {report.ScannedTarget}");
        sb.AppendLine($"Files Scanned: {report.Documents.Count}");
        sb.AppendLine($"Deterministic Risk Score: {report.RiskScore}/100 ({report.OverallSeverity})");
        sb.AppendLine();
        sb.AppendLine("=== DETERMINISTIC AST FINDINGS ===");

        if (report.Findings.Count == 0)
        {
            sb.AppendLine("No deterministic rule violations detected. Verify there are no subtle semantic/locking hazards.");
        }
        else
        {
            foreach (var f in report.Findings.Take(25))
            {
                sb.AppendLine($"- [{f.RuleId}][{f.Severity}] {Path.GetFileName(f.FilePath)}:{f.LineNumber} — {f.Title}");
                sb.AppendLine($"  Impact: {f.EstimatedImpact}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("=== MIGRATION EXCERPTS ===");
        foreach (var doc in report.Documents.Take(5))
        {
            sb.AppendLine($"--- File: {Path.GetFileName(doc.FilePath)} ({doc.SourceType}, {doc.Dialect}) ---");
            var excerpt = doc.Content.Length > 2500 ? doc.Content[..2500] + "\n/* ...truncated... */" : doc.Content;
            sb.AppendLine(excerpt);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    public static AiReviewResult ParseStructuredResponse(string provider, string model, string rawContent)
    {
        var cleaned = StripCodeFences(rawContent).Trim();

        try
        {
            using var doc = JsonDocument.Parse(cleaned);
            var root = doc.RootElement;

            var summary = root.TryGetProperty("executiveSummary", out var sumEl)
                ? sumEl.GetString() ?? cleaned
                : cleaned;

            var additionalRisks = ExtractStringList(root, "additionalRisks");
            var rolloutChecklist = ExtractStringList(root, "rolloutChecklist");
            var mitigationSql = root.TryGetProperty("suggestedMitigationSql", out var sqlEl) && sqlEl.ValueKind == JsonValueKind.String
                ? sqlEl.GetString()
                : null;

            return new AiReviewResult
            {
                Provider = provider,
                Model = model,
                ExecutiveSummary = summary,
                AdditionalRisks = additionalRisks,
                RolloutChecklist = rolloutChecklist,
                SuggestedMitigationSql = mitigationSql,
                Succeeded = true
            };
        }
        catch (JsonException)
        {
            // Fallback gracefully if a smaller local model returns prose instead of strict JSON
            return new AiReviewResult
            {
                Provider = provider,
                Model = model,
                ExecutiveSummary = cleaned,
                Succeeded = true
            };
        }
    }

    private static List<string> ExtractStringList(JsonElement root, string propertyName)
    {
        var list = new List<string>();
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var val = item.GetString();
                if (!string.IsNullOrWhiteSpace(val))
                    list.Add(val);
            }
        }
        return list;
    }

    private static string StripCodeFences(string text)
    {
        var trimmed = text.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;

        int firstNewline = trimmed.IndexOf('\n');
        if (firstNewline < 0)
            return trimmed;

        var inner = trimmed[(firstNewline + 1)..];
        int lastFence = inner.LastIndexOf("```", StringComparison.Ordinal);
        return lastFence >= 0 ? inner[..lastFence] : inner;
    }
}
