namespace MigrationSentinel.Core.Models;

public sealed record RiskFinding
{
    public required string RuleId { get; init; }
    public required string RuleName { get; init; }
    public required RiskSeverity Severity { get; init; }
    public required RiskCategory Category { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string FilePath { get; init; }
    public required int LineNumber { get; init; }
    public string CodeSnippet { get; init; } = string.Empty;
    public required string Recommendation { get; init; }
    public string? MitigationScript { get; init; }
    public string EstimatedImpact { get; init; } = string.Empty;
    public MigrationOperation? Operation { get; init; }
}
