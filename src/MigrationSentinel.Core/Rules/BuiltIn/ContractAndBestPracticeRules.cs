using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Core.Rules.BuiltIn;

public sealed class RenameObjectRule : IRiskRule
{
    public string RuleId => "MS006";
    public string Name => "Breaking Table or Column Rename";
    public string Description => "Detects table or column renames (RENAME TABLE/COLUMN, sp_rename) that immediately break running application instances.";
    public RiskCategory Category => RiskCategory.BreakingContract;
    public RiskSeverity DefaultSeverity => RiskSeverity.High;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType is not (MigrationOperationType.RenameTable or MigrationOperationType.RenameColumn))
            yield break;

        bool isTable = operation.OperationType == MigrationOperationType.RenameTable;
        var table = operation.QualifiedTableName;
        var oldName = isTable ? table : $"{table}.{operation.ColumnName}";
        var newName = operation.NewName ?? "<new_name>";

        var mitigation = isTable
            ? $"""
               -- Zero-Downtime Table Rename Plan ('{oldName}' -> '{newName}'):
               -- 1. Create a compatibility VIEW or dual-write table so old and new app versions operate concurrently.
               -- 2. Roll out application code pointing to '{newName}'.
               -- 3. Drop the compatibility view / old table only after all old pods terminate.
               """
            : $"""
               -- Zero-Downtime Column Rename Plan ('{oldName}' -> '{newName}'):
               -- 1. Add new column '{newName}' alongside '{operation.ColumnName}'.
               -- 2. Backfill '{newName}' and keep both columns synchronized via dual-write or trigger.
               -- 3. Switch application reads to '{newName}' in Release N+1.
               -- 4. Drop legacy column '{operation.ColumnName}' in Release N+2.
               """;

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = DefaultSeverity,
            Category = Category,
            Title = $"Renaming {(isTable ? "table" : "column")} '{oldName}' to '{newName}' breaks zero-downtime deployments",
            Description = $"In-place renames invalidate all queries from currently running application instances, views, stored procedures, and downstream BI/ETL pipelines referencing '{oldName}'.",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Use an additive expand-and-contract workflow (add new column/view, dual-write, cut over, then remove the legacy name in a later deployment).",
            MitigationScript = mitigation,
            EstimatedImpact = "Immediate runtime SQL errors during rolling/blue-green deployments + broken downstream consumers",
            Operation = operation
        };
    }
}

public sealed class DropConstraintRule : IRiskRule
{
    public string RuleId => "MS010";
    public string Name => "Primary / Foreign Key Constraint Removal";
    public string Description => "Detects dropping Primary Key, Foreign Key, or table constraints that guard data integrity.";
    public RiskCategory Category => RiskCategory.BreakingContract;
    public RiskSeverity DefaultSeverity => RiskSeverity.Medium;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType is not (MigrationOperationType.DropPrimaryKey or MigrationOperationType.DropForeignKey or MigrationOperationType.DropConstraint))
            yield break;

        var table = operation.QualifiedTableName;
        var name = operation.ConstraintName ?? "<constraint>";
        var isPk = operation.OperationType == MigrationOperationType.DropPrimaryKey;

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = isPk ? RiskSeverity.High : DefaultSeverity,
            Category = Category,
            Title = $"Dropping {(isPk ? "primary key" : "constraint")} '{name}' on '{table}'",
            Description = isPk
                ? $"Dropping primary key '{name}' on '{table}' breaks replication topologies (e.g., logical replication, CDC, AlwaysOn read workloads), causes exclusive locking, and removes row uniqueness guarantees."
                : $"Dropping constraint '{name}' on '{table}' removes database-enforced referential or domain integrity and requires a schema modification lock.",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Verify dependent foreign keys, CDC/replication slots, and replace constraints safely with a bounded LOCK_TIMEOUT.",
            MitigationScript = $"-- Ensure replacement constraint/key is planned and wrap metadata changes with a lock timeout.\n-- Verify dependent foreign keys referencing '{table}' before dropping '{name}'.",
            EstimatedImpact = isPk
                ? "Replication breakage + exclusive lock + loss of entity uniqueness"
                : "Loss of referential integrity + schema modification lock",
            Operation = operation
        };
    }
}

public sealed class RawSqlInEfMigrationRule : IRiskRule
{
    public string RuleId => "MS013";
    public string Name => "Raw SQL Embedded in EF Core Migration";
    public string Description => "Flags raw migrationBuilder.Sql() calls for explicit DBA review and idempotency verification.";
    public RiskCategory Category => RiskCategory.BestPractice;
    public RiskSeverity DefaultSeverity => RiskSeverity.Low;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType != MigrationOperationType.RawSql)
            yield break;

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = DefaultSeverity,
            Category = Category,
            Title = "Raw SQL statement executed via migrationBuilder.Sql()",
            Description = "Raw SQL bypasses EF Core's model snapshot diffing and provider abstractions. Ensure the statement is idempotent, dialect-compatible, and paired with a matching rollback in Down().",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Review the embedded SQL carefully (MigrationSentinel also automatically parses its AST for destructive operations) and verify idempotent script generation (`dotnet ef migrations script --idempotent`).",
            MitigationScript = null,
            EstimatedImpact = "Potential schema drift from EF ModelSnapshot or non-idempotent re-execution",
            Operation = operation
        };
    }
}

public sealed class MissingDownMigrationRule : IRiskRule
{
    public string RuleId => "MS014";
    public string Name => "Missing or Empty Down() Rollback Method";
    public string Description => "Detects EF Core migrations whose Down() method is missing or empty, preventing automated rollback.";
    public RiskCategory Category => RiskCategory.BestPractice;
    public RiskSeverity DefaultSeverity => RiskSeverity.Low;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType != MigrationOperationType.MissingDownMigration)
            yield break;

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = DefaultSeverity,
            Category = Category,
            Title = $"EF Core migration '{operation.TableName ?? document.MigrationClassName}' has an empty or missing Down() method",
            Description = "Without a reversible Down() implementation, incident rollback via `dotnet ef database update <PreviousMigration>` cannot undo schema changes.",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Implement compensating rollback operations in Down(), or explicitly document why this migration requires a forward-only fix.",
            MitigationScript = null,
            EstimatedImpact = "Delayed incident recovery / inability to execute automated schema rollback",
            Operation = operation
        };
    }
}
