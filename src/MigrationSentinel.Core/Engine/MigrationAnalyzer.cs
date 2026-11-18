using System.Diagnostics;
using System.Text;
using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Core.Models;
using MigrationSentinel.Core.Parsing;
using MigrationSentinel.Core.Rules;

namespace MigrationSentinel.Core.Engine;

public sealed class MigrationAnalyzer
{
    private readonly IMigrationParser _parser;
    private readonly IReadOnlyList<IRiskRule> _rules;

    public MigrationAnalyzer(
        IMigrationParser? parser = null,
        IEnumerable<IRiskRule>? rules = null)
    {
        _parser = parser ?? new CompositeMigrationParser();
        _rules = rules?.ToList() ?? RuleCatalog.CreateDefaultRules();
    }

    public IReadOnlyList<IRiskRule> Rules => _rules;

    public AnalysisReport AnalyzeContent(
        string filePath,
        string content,
        SentinelOptions? options = null)
    {
        var sw = Stopwatch.StartNew();
        options ??= new SentinelOptions();

        var doc = _parser.Parse(filePath, content, options.Dialect);
        var findings = EvaluateDocument(doc, options);
        sw.Stop();

        return BuildReport(filePath, [doc], findings, sw.Elapsed);
    }

    public async Task<AnalysisReport> AnalyzeFilesAsync(
        string scannedTarget,
        IEnumerable<string> filePaths,
        SentinelOptions? options = null,
        Action<string>? onFileScanned = null,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        options ??= new SentinelOptions();

        var documents = new List<MigrationDocument>();
        var findings = new List<RiskFinding>();

        foreach (var path in filePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            onFileScanned?.Invoke(path);

            var content = await File.ReadAllTextAsync(path, cancellationToken);
            if (!_parser.CanParse(path, content))
                continue;

            var doc = _parser.Parse(path, content, options.Dialect);
            documents.Add(doc);
            findings.AddRange(EvaluateDocument(doc, options));
        }

        sw.Stop();
        return BuildReport(scannedTarget, documents, findings, sw.Elapsed);
    }

    public static string GenerateConsolidatedMitigationScript(AnalysisReport report)
    {
        var sb = new StringBuilder();
        sb.AppendLine("-- =============================================================================");
        sb.AppendLine("-- MigrationSentinel — Auto-Generated Safe Rollout & Mitigation Script");
        sb.AppendLine($"-- Target:    {report.ScannedTarget}");
        sb.AppendLine($"-- Generated: {report.TimestampUtc:O}");
        sb.AppendLine($"-- Findings:  {report.Findings.Count} (Risk Score: {report.RiskScore}/100 — {report.OverallSeverity})");
        sb.AppendLine("-- =============================================================================");
        sb.AppendLine();

        var withMitigation = report.Findings
            .Where(f => !string.IsNullOrWhiteSpace(f.MitigationScript))
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.FilePath)
            .ThenBy(f => f.LineNumber)
            .ToList();

        if (withMitigation.Count == 0)
        {
            sb.AppendLine("-- No automated mitigation scripts required. All checked migrations look safe!");
            return sb.ToString();
        }

        foreach (var finding in withMitigation)
        {
            sb.AppendLine($"-- [{finding.RuleId}][{finding.Severity.ToString().ToUpperInvariant()}] {finding.Title}");
            sb.AppendLine($"-- Source: {finding.FilePath}:{finding.LineNumber}");
            sb.AppendLine(finding.MitigationScript!.Trim());
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(report.AiReview?.SuggestedMitigationSql))
        {
            sb.AppendLine("-- =============================================================================");
            sb.AppendLine($"-- AI-Augmented Mitigation Recommendations ({report.AiReview.Provider} / {report.AiReview.Model})");
            sb.AppendLine("-- =============================================================================");
            sb.AppendLine(report.AiReview.SuggestedMitigationSql.Trim());
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private List<RiskFinding> EvaluateDocument(MigrationDocument doc, SentinelOptions options)
    {
        var results = new List<RiskFinding>();

        foreach (var op in doc.Operations)
        {
            foreach (var rule in _rules)
            {
                if (options.DisabledRules.Contains(rule.RuleId))
                    continue;

                foreach (var finding in rule.Evaluate(doc, op))
                {
                    var adjusted = options.SeverityOverrides.TryGetValue(rule.RuleId, out var overrideSev)
                        ? finding with { Severity = overrideSev }
                        : finding;
                    results.Add(adjusted);
                }
            }
        }

        return results
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.LineNumber)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ToList();
    }

    private static AnalysisReport BuildReport(
        string target,
        IReadOnlyList<MigrationDocument> documents,
        IReadOnlyList<RiskFinding> findings,
        TimeSpan duration)
    {
        var sortedFindings = findings
            .OrderByDescending(f => f.Severity)
            .ThenBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.LineNumber)
            .ToList();

        int rawScore = sortedFindings.Sum(f => f.Severity switch
        {
            RiskSeverity.Critical => 35,
            RiskSeverity.High => 20,
            RiskSeverity.Medium => 10,
            RiskSeverity.Low => 4,
            _ => 1
        });

        int riskScore = Math.Min(100, rawScore);
        var overallSeverity = sortedFindings.Count == 0
            ? RiskSeverity.Info
            : sortedFindings.Max(f => f.Severity);

        return new AnalysisReport
        {
            ScannedTarget = target,
            TimestampUtc = DateTimeOffset.UtcNow,
            Duration = duration,
            Documents = documents,
            Findings = sortedFindings,
            RiskScore = riskScore,
            OverallSeverity = overallSeverity
        };
    }
}
