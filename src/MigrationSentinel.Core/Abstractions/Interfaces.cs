using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Core.Abstractions;

/// <summary>
/// Parses a migration artifact into a normalized <see cref="MigrationDocument"/>.
/// </summary>
public interface IMigrationParser
{
    bool CanParse(string filePath, string content);
    MigrationDocument Parse(string filePath, string content, DatabaseDialect dialectHint = DatabaseDialect.Auto);
}

/// <summary>
/// Evaluates a single migration risk rule against a parsed operation.
/// </summary>
public interface IRiskRule
{
    string RuleId { get; }
    string Name { get; }
    string Description { get; }
    RiskCategory Category { get; }
    RiskSeverity DefaultSeverity { get; }
    IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation);
}

/// <summary>
/// Discovers migration files on disk.
/// </summary>
public interface IMigrationFileScanner
{
    Task<IReadOnlyList<string>> ScanAsync(string targetPath, CancellationToken cancellationToken = default);
}

/// <summary>
/// Identifies added or modified migration files from a Git repository diff.
/// </summary>
public interface IGitDiffAnalyzer
{
    Task<IReadOnlyList<string>> GetChangedMigrationFilesAsync(
        string repositoryPath,
        string? baseRef = null,
        string? headRef = null,
        bool stagedOnly = false,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Calls an LLM backend (OpenAI, Ollama, etc.) to produce an augmented migration risk review.
/// </summary>
public interface IAiReviewProvider
{
    string ProviderName { get; }
    Task<AiReviewResult> ReviewAsync(AnalysisReport deterministicReport, CancellationToken cancellationToken = default);
}

/// <summary>
/// Serializes an <see cref="AnalysisReport"/> into an external output format (JSON, Markdown, SARIF).
/// </summary>
public interface IReportFormatter
{
    string FormatName { get; }
    string Format(AnalysisReport report);
}
