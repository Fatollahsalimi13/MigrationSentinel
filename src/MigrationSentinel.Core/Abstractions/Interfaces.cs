using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Core.Abstractions;

public interface IMigrationParser
{
    bool CanParse(string filePath, string content);
    MigrationDocument Parse(string filePath, string content, DatabaseDialect dialectHint = DatabaseDialect.Auto);
}

public interface IRiskRule
{
    string RuleId { get; }
    string Name { get; }
    string Description { get; }
    RiskCategory Category { get; }
    RiskSeverity DefaultSeverity { get; }
    IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation);
}

public interface IMigrationFileScanner
{
    Task<IReadOnlyList<string>> ScanAsync(string targetPath, CancellationToken cancellationToken = default);
}

public interface IGitDiffAnalyzer
{
    Task<IReadOnlyList<string>> GetChangedMigrationFilesAsync(
        string repositoryPath,
        string? baseRef = null,
        string? headRef = null,
        bool stagedOnly = false,
        CancellationToken cancellationToken = default);
}

public interface IAiReviewProvider
{
    string ProviderName { get; }
    Task<AiReviewResult> ReviewAsync(AnalysisReport deterministicReport, CancellationToken cancellationToken = default);
}

public interface IReportFormatter
{
    string FormatName { get; }
    string Format(AnalysisReport report);
}
