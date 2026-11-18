using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Core.Rules.BuiltIn;

public sealed class DropTableRule : IRiskRule
{
    public string RuleId => "MS001";
    public string Name => "Destructive Table Drop";
    public string Description => "Detects DROP TABLE operations that permanently delete data and break running application instances.";
    public RiskCategory Category => RiskCategory.DataLoss;
    public RiskSeverity DefaultSeverity => RiskSeverity.Critical;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType != MigrationOperationType.DropTable)
            yield break;

        var table = operation.QualifiedTableName;
        var mitigation = document.Dialect == DatabaseDialect.PostgreSql
            ? $"""
               -- Phase 1 (Zero-Downtime): Stop reading/writing "{table}" in application code first.
               -- Phase 2: Archive table instead of immediate DROP:
               ALTER TABLE {table} RENAME TO {operation.TableName ?? "target"}_deprecated_{DateTime.UtcNow:yyyyMMdd};
               -- Phase 3 (After retention window & verified zero traffic):
               -- DROP TABLE IF EXISTS {operation.TableName ?? "target"}_deprecated_{DateTime.UtcNow:yyyyMMdd};
               """
            : $"""
               -- Phase 1 (Zero-Downtime): Deploy application version that no longer references [{table}].
               -- Phase 2: Archive/quarantine the table before dropping:
               EXEC sp_rename '{table}', '{operation.TableName ?? "target"}_deprecated_{DateTime.UtcNow:yyyyMMdd}';
               -- Phase 3 (After backup & retention window):
               -- DROP TABLE IF EXISTS [{operation.TableName ?? "target"}_deprecated_{DateTime.UtcNow:yyyyMMdd}];
               """;

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = DefaultSeverity,
            Category = Category,
            Title = $"Dropping table '{table}' causes irreversible data loss",
            Description = $"DROP TABLE on '{table}' permanently removes all rows, indexes, and constraints, and will immediately fail any live application instances still querying this table during rolling deployments.",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Use the Expand-and-Contract pattern: remove all application references first, archive or rename the table during a deprecation window, and drop only after verified zero reads/writes and a verified backup.",
            MitigationScript = mitigation,
            EstimatedImpact = "Irreversible data loss + immediate 500 errors on active app nodes + exclusive schema lock",
            Operation = operation
        };
    }
}

public sealed class DropColumnRule : IRiskRule
{
    public string RuleId => "MS002";
    public string Name => "Destructive Column Drop";
    public string Description => "Detects DROP COLUMN operations that permanently delete column data and break active ORM mappings.";
    public RiskCategory Category => RiskCategory.DataLoss;
    public RiskSeverity DefaultSeverity => RiskSeverity.Critical;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType != MigrationOperationType.DropColumn)
            yield break;

        var table = operation.QualifiedTableName;
        var col = operation.ColumnName ?? "<unknown>";
        var mitigation = document.Dialect == DatabaseDialect.PostgreSql
            ? $"""
               -- Safe 2-Release Expand-and-Contract Plan for "{table}"."{col}":
               -- Release 1: Remove '{col}' from EF Core entity / SELECT projections and make column nullable:
               ALTER TABLE {table} ALTER COLUMN "{col}" DROP NOT NULL;
               -- Release 2 (Only after all running pods no longer read/write '{col}'):
               -- SET lock_timeout = '3s';
               -- ALTER TABLE {table} DROP COLUMN IF EXISTS "{col}";
               """
            : $"""
               -- Safe 2-Release Expand-and-Contract Plan for [{table}].[{col}]:
               -- Release 1: Remove '{col}' from EF Core entity & queries (ignore column in DbContext) and allow NULLs.
               -- Release 2 (After full rollout & backup snapshot):
               SET LOCK_TIMEOUT 3000;
               -- Drop dependent default/check constraints first, then drop column:
               -- ALTER TABLE {table} DROP COLUMN [{col}];
               """;

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = DefaultSeverity,
            Category = Category,
            Title = $"Dropping column '{col}' from '{table}' causes data loss & ORM contract breakage",
            Description = $"Dropping '{table}.{col}' permanently discards existing values and breaks any running EF Core or SQL queries selecting all columns before the new application binary finishes rolling out.",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Decouple code and schema deploys: stop selecting/writing the column in Release N (mark it nullable), then drop the column in Release N+1 with a lock timeout configured.",
            MitigationScript = mitigation,
            EstimatedImpact = "Permanent column data loss + InvalidColumnName exceptions during rolling deployment",
            Operation = operation
        };
    }
}

public sealed class TruncateTableRule : IRiskRule
{
    public string RuleId => "MS003";
    public string Name => "Irreversible Table Truncation";
    public string Description => "Detects TRUNCATE TABLE statements inside migrations, which wipe all rows under an exclusive lock.";
    public RiskCategory Category => RiskCategory.DataLoss;
    public RiskSeverity DefaultSeverity => RiskSeverity.Critical;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType != MigrationOperationType.TruncateTable)
            yield break;

        var table = operation.QualifiedTableName;
        var mitigation = $"""
            -- Avoid TRUNCATE TABLE in automated schema migrations.
            -- If data cleanup is strictly required, back up data first and delete in bounded batches:
            -- CREATE TABLE {operation.TableName ?? "target"}_backup AS SELECT * FROM {table};
            -- Perform batched DELETE with an explicit WHERE predicate outside peak traffic.
            """;

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = DefaultSeverity,
            Category = Category,
            Title = $"TRUNCATE TABLE '{table}' wipes all data irreversibly",
            Description = $"TRUNCATE TABLE '{table}' removes every row without firing row triggers and acquires an exclusive table lock (ACCESS EXCLUSIVE / SCH-M).",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Remove TRUNCATE from schema migrations. Use an audited data-archival job with bounded batched deletes and explicit WHERE conditions.",
            MitigationScript = mitigation,
            EstimatedImpact = "Complete table data loss + exclusive table lock blocking all queries",
            Operation = operation
        };
    }
}

public sealed class UnfilteredDmlRule : IRiskRule
{
    public string RuleId => "MS008";
    public string Name => "Unfiltered Bulk DELETE/UPDATE";
    public string Description => "Detects DELETE or UPDATE statements missing a WHERE clause inside migration scripts.";
    public RiskCategory Category => RiskCategory.DataLoss;
    public RiskSeverity DefaultSeverity => RiskSeverity.High;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType is not (MigrationOperationType.UnfilteredDelete or MigrationOperationType.UnfilteredUpdate))
            yield break;

        bool isDelete = operation.OperationType == MigrationOperationType.UnfilteredDelete;
        var verb = isDelete ? "DELETE" : "UPDATE";
        var table = operation.QualifiedTableName;

        var mitigation = document.Dialect == DatabaseDialect.PostgreSql
            ? $"""
               -- Always scope {verb} with a deterministic WHERE predicate and execute in bounded batches:
               -- WITH batch AS (
               --     SELECT id FROM {table} WHERE <condition> LIMIT 5000
               -- )
               -- {(isDelete ? $"DELETE FROM {table} USING batch WHERE {table}.id = batch.id;" : $"UPDATE {table} SET <col> = <val> FROM batch WHERE {table}.id = batch.id;")}
               """
            : $"""
               -- Always scope {verb} with a WHERE clause and chunk large tables to avoid lock escalation & log growth:
               -- WHILE 1 = 1
               -- BEGIN
               --     {(isDelete ? $"DELETE TOP (5000) FROM {table} WHERE <condition>;" : $"UPDATE TOP (5000) {table} SET <col> = <val> WHERE <condition>;")}
               --     IF @@ROWCOUNT = 0 BREAK;
               -- END
               """;

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = isDelete ? RiskSeverity.Critical : DefaultSeverity,
            Category = Category,
            Title = $"Unfiltered {verb} on '{table}' without a WHERE clause",
            Description = $"Executing {verb} against '{table}' without a WHERE clause affects every row in the table, risking massive data corruption, transaction log exhaustion, and table-wide lock escalation.",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = $"Add an explicit WHERE filter and run large {verb} operations in chunked batches (e.g., 5,000 rows per transaction).",
            MitigationScript = mitigation,
            EstimatedImpact = "Full-table mutation + lock escalation + transaction log spike",
            Operation = operation
        };
    }
}

public sealed class CascadeDeleteRule : IRiskRule
{
    public string RuleId => "MS012";
    public string Name => "Cascading Delete Risk";
    public string Description => "Detects CASCADE clauses on DROP statements or ON DELETE CASCADE foreign keys that can trigger unintended multi-table deletions.";
    public RiskCategory Category => RiskCategory.Security;
    public RiskSeverity DefaultSeverity => RiskSeverity.Medium;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (!operation.IsCascade)
            yield break;

        var table = operation.QualifiedTableName;
        var context = operation.OperationType == MigrationOperationType.AddForeignKey
            ? $"Foreign key on '{table}' configured with ON DELETE CASCADE"
            : $"Destructive {operation.OperationType} on '{table}' specifies CASCADE";

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = DefaultSeverity,
            Category = Category,
            Title = context,
            Description = "CASCADE automatically propagates deletions to dependent objects or child rows, which can cause silent data loss and long-running lock chains on heavily referenced tables.",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Prefer RESTRICT / NO ACTION (ReferentialAction.Restrict) and handle dependent cleanup explicitly in application logic or soft-delete workflows.",
            MitigationScript = "-- Replace ON DELETE CASCADE / ReferentialAction.Cascade with ON DELETE RESTRICT (or NO ACTION)\n-- and explicitly archive or delete child rows in controlled batches.",
            EstimatedImpact = "Unintended recursive deletion of child records or dependent views/constraints",
            Operation = operation
        };
    }
}
