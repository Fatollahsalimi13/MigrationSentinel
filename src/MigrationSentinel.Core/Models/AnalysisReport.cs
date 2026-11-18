namespace MigrationSentinel.Core.Models;

public sealed record AiReviewResult
{
    public required string Provider { get; init; }
    public required string Model { get; init; }
    public required string ExecutiveSummary { get; init; }
    public IReadOnlyList<string> AdditionalRisks { get; init; } = [];
    public IReadOnlyList<string> RolloutChecklist { get; init; } = [];
    public string? SuggestedMitigationSql { get; init; }
    public bool Succeeded { get; init; } = true;
    public string? ErrorMessage { get; init; }
}

public sealed record AnalysisReport
{
    public required string ScannedTarget { get; init; }
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<MigrationDocument> Documents { get; init; } = [];
    public IReadOnlyList<RiskFinding> Findings { get; init; } = [];
    public int RiskScore { get; init; }
    public RiskSeverity OverallSeverity { get; init; }
    public AiReviewResult? AiReview { get; init; }

    public int CriticalCount => Findings.Count(f => f.Severity == RiskSeverity.Critical);
    public int HighCount => Findings.Count(f => f.Severity == RiskSeverity.High);
    public int MediumCount => Findings.Count(f => f.Severity == RiskSeverity.Medium);
    public int LowCount => Findings.Count(f => f.Severity == RiskSeverity.Low);
    public int InfoCount => Findings.Count(f => f.Severity == RiskSeverity.Info);

    public bool ExceedsThreshold(RiskSeverity? failOnSeverity) =>
        failOnSeverity.HasValue && Findings.Any(f => f.Severity >= failOnSeverity.Value);
}

public sealed record SentinelOptions
{
    public DatabaseDialect Dialect { get; init; } = DatabaseDialect.Auto;
    public HashSet<string> DisabledRules { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, RiskSeverity> SeverityOverrides { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public RiskSeverity? FailOnSeverity { get; init; } = RiskSeverity.High;
}
