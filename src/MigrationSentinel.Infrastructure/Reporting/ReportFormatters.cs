using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Infrastructure.Reporting;

public sealed class JsonReportFormatter : IReportFormatter
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public string FormatName => "json";

    public string Format(AnalysisReport report)
    {
        var dto = new
        {
            tool = "MigrationSentinel",
            version = "1.0.0",
            scannedTarget = report.ScannedTarget,
            timestampUtc = report.TimestampUtc,
            durationMs = Math.Round(report.Duration.TotalMilliseconds, 2),
            summary = new
            {
                filesScanned = report.Documents.Count,
                totalFindings = report.Findings.Count,
                riskScore = report.RiskScore,
                overallSeverity = report.OverallSeverity,
                counts = new
                {
                    critical = report.CriticalCount,
                    high = report.HighCount,
                    medium = report.MediumCount,
                    low = report.LowCount,
                    info = report.InfoCount
                }
            },
            findings = report.Findings.Select(f => new
            {
                ruleId = f.RuleId,
                ruleName = f.RuleName,
                severity = f.Severity,
                category = f.Category,
                title = f.Title,
                description = f.Description,
                filePath = f.FilePath,
                lineNumber = f.LineNumber,
                estimatedImpact = f.EstimatedImpact,
                recommendation = f.Recommendation,
                mitigationScript = f.MitigationScript
            }),
            aiReview = report.AiReview
        };

        return JsonSerializer.Serialize(dto, SerializerOptions);
    }
}

public sealed class MarkdownReportFormatter : IReportFormatter
{
    public string FormatName => "markdown";

    public string Format(AnalysisReport report)
    {
        var sb = new StringBuilder();
        var statusIcon = report.OverallSeverity switch
        {
            RiskSeverity.Critical => "🔴",
            RiskSeverity.High => "🟠",
            RiskSeverity.Medium => "🟡",
            RiskSeverity.Low => "🔵",
            _ => "🟢"
        };

        sb.AppendLine("# 🛡️ MigrationSentinel Risk Report");
        sb.AppendLine();
        sb.AppendLine($"**Overall Risk:** {statusIcon} **{report.OverallSeverity}** (`{report.RiskScore}/100`)  ");
        sb.AppendLine($"**Target:** `{report.ScannedTarget}` | **Files Scanned:** `{report.Documents.Count}` | **Findings:** `{report.Findings.Count}` | **Duration:** `{report.Duration.TotalMilliseconds:F0}ms`");
        sb.AppendLine();
        sb.AppendLine("| Severity | Count |");
        sb.AppendLine("| :--- | :---: |");
        sb.AppendLine($"| 🔴 Critical | {report.CriticalCount} |");
        sb.AppendLine($"| 🟠 High | {report.HighCount} |");
        sb.AppendLine($"| 🟡 Medium | {report.MediumCount} |");
        sb.AppendLine($"| 🔵 Low | {report.LowCount} |");
        sb.AppendLine();

        if (report.Findings.Count == 0)
        {
            sb.AppendLine("✅ **No migration risks detected!** All scanned migrations conform to zero-downtime safety rules.");
            return sb.ToString();
        }

        sb.AppendLine("## Findings");
        sb.AppendLine();
        sb.AppendLine("| Rule | Severity | Category | Location | Finding |");
        sb.AppendLine("| :--- | :--- | :--- | :--- | :--- |");

        foreach (var f in report.Findings)
        {
            var fileName = Path.GetFileName(f.FilePath);
            sb.AppendLine($"| `{f.RuleId}` | **{f.Severity}** | `{f.Category}` | `{fileName}:{f.LineNumber}` | {EscapePipe(f.Title)} |");
        }

        sb.AppendLine();
        sb.AppendLine("## Detailed Recommendations & Mitigation Scripts");
        sb.AppendLine();

        foreach (var f in report.Findings)
        {
            sb.AppendLine($"### `{f.RuleId}` — {f.Title}");
            sb.AppendLine($"- **File:** `{f.FilePath}:{f.LineNumber}`");
            sb.AppendLine($"- **Severity:** `{f.Severity}` | **Category:** `{f.Category}`");
            sb.AppendLine($"- **Impact:** {f.EstimatedImpact}");
            sb.AppendLine($"- **Recommendation:** {f.Recommendation}");

            if (!string.IsNullOrWhiteSpace(f.MitigationScript))
            {
                sb.AppendLine();
                sb.AppendLine("<details>");
                sb.AppendLine("<summary>Suggested Safe Rollout / Mitigation SQL</summary>");
                sb.AppendLine();
                sb.AppendLine("```sql");
                sb.AppendLine(f.MitigationScript.Trim());
                sb.AppendLine("```");
                sb.AppendLine("</details>");
            }

            sb.AppendLine();
        }

        if (report.AiReview is not null)
        {
            sb.AppendLine($"## 🤖 AI Review ({report.AiReview.Provider} — `{report.AiReview.Model}`)");
            sb.AppendLine();
            sb.AppendLine(report.AiReview.ExecutiveSummary);
            sb.AppendLine();

            if (report.AiReview.RolloutChecklist.Count > 0)
            {
                sb.AppendLine("### Rollout Checklist");
                foreach (var step in report.AiReview.RolloutChecklist)
                    sb.AppendLine($"- [ ] {step}");
                sb.AppendLine();
            }
        }

        return sb.ToString();
    }

    private static string EscapePipe(string input) => input.Replace("|", "\\|");
}

/// <summary>
/// Exports analysis findings in SARIF v2.1.0 format for GitHub Code Scanning &amp; IDE integration.
/// </summary>
public sealed class SarifReportFormatter : IReportFormatter
{
    public string FormatName => "sarif";

    public string Format(AnalysisReport report)
    {
        var rules = report.Findings
            .GroupBy(f => f.RuleId)
            .Select(g =>
            {
                var first = g.First();
                return new
                {
                    id = first.RuleId,
                    name = first.RuleName,
                    shortDescription = new { text = first.RuleName },
                    fullDescription = new { text = first.Description },
                    defaultConfiguration = new { level = MapSarifLevel(first.Severity) },
                    properties = new
                    {
                        category = first.Category.ToString(),
                        securitySeverity = MapSecuritySeverityScore(first.Severity)
                    }
                };
            })
            .ToArray();

        var results = report.Findings.Select(f => new
        {
            ruleId = f.RuleId,
            level = MapSarifLevel(f.Severity),
            message = new { text = $"{f.Title}. {f.Recommendation}" },
            locations = new[]
            {
                new
                {
                    physicalLocation = new
                    {
                        artifactLocation = new
                        {
                            uri = ToRelativeUri(f.FilePath)
                        },
                        region = new
                        {
                            startLine = Math.Max(1, f.LineNumber)
                        }
                    }
                }
            }
        }).ToArray();

        var sarif = new Dictionary<string, object>
        {
            ["$schema"] = "https://json.schemastore.org/sarif-2.1.0.json",
            ["version"] = "2.1.0",
            ["runs"] = new[]
            {
                new
                {
                    tool = new
                    {
                        driver = new
                        {
                            name = "MigrationSentinel",
                            version = "1.0.0",
                            informationUri = "https://github.com/soghrazakeran4-bot/MigrationSentinel",
                            rules
                        }
                    },
                    results
                }
            }
        };

        return JsonSerializer.Serialize(sarif, new JsonSerializerOptions { WriteIndented = true });
    }

    private static string MapSarifLevel(RiskSeverity severity) => severity switch
    {
        RiskSeverity.Critical or RiskSeverity.High => "error",
        RiskSeverity.Medium => "warning",
        _ => "note"
    };

    private static string MapSecuritySeverityScore(RiskSeverity severity) => severity switch
    {
        RiskSeverity.Critical => "9.5",
        RiskSeverity.High => "7.5",
        RiskSeverity.Medium => "5.0",
        RiskSeverity.Low => "2.5",
        _ => "1.0"
    };

    private static string ToRelativeUri(string path)
    {
        try
        {
            var cwd = Directory.GetCurrentDirectory();
            var rel = Path.GetRelativePath(cwd, path);
            if (!rel.StartsWith("..", StringComparison.Ordinal))
                return rel.Replace('\\', '/');
        }
        catch
        {
            // Fall back to normalized path
        }
        return path.Replace('\\', '/');
    }
}
