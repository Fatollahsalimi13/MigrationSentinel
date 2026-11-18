using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Core.Rules.BuiltIn;

public sealed class AlterColumnTypeRule : IRiskRule
{
    public string RuleId => "MS004";
    public string Name => "Breaking Column Type / Precision Change";
    public string Description => "Detects ALTER COLUMN data type or narrowing length changes that trigger full table rewrites and potential data truncation.";
    public RiskCategory Category => RiskCategory.TableLock;
    public RiskSeverity DefaultSeverity => RiskSeverity.High;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType != MigrationOperationType.AlterColumnType)
            yield break;

        var table = operation.QualifiedTableName;
        var col = operation.ColumnName ?? "<unknown>";
        var newType = operation.DataType ?? "new_type";
        bool isNarrowing = operation.MaxLength.HasValue &&
                           operation.OldMaxLength.HasValue &&
                           operation.MaxLength.Value < operation.OldMaxLength.Value;

        var severity = isNarrowing ? RiskSeverity.Critical : DefaultSeverity;

        var mitigation = document.Dialect == DatabaseDialect.PostgreSql
            ? $"""
               -- Zero-Downtime Expand-and-Contract for altering "{table}"."{col}" to {newType}:
               -- 1. Add shadow column (metadata-only in PostgreSQL):
               ALTER TABLE {table} ADD COLUMN "{col}_v2" {newType} NULL;
               -- 2. Dual-write via application or trigger, then backfill in batches of 5,000 rows.
               -- 3. Switch reads to "{col}_v2", validate parity, then atomically swap/drop old column.
               """
            : $"""
               -- Zero-Downtime Expand-and-Contract for altering [{table}].[{col}] to {newType}:
               -- 1. Add nullable shadow column:
               ALTER TABLE {table} ADD [{col}_v2] {newType} NULL;
               -- 2. Dual-write and backfill [{col}_v2] in 5,000-row batches to avoid SCH-M table rewrite lock.
               -- 3. Cut over application reads/writes to [{col}_v2], then drop [{col}] in a follow-up release.
               """;

        var detail = isNarrowing
            ? $"Narrowing '{table}.{col}' from length {operation.OldMaxLength} to {operation.MaxLength} ({newType}) will fail or silently truncate existing rows and acquires an exclusive table lock."
            : $"Altering '{table}.{col}' to '{newType}' typically requires a full table rewrite under an exclusive lock (ACCESS EXCLUSIVE in PostgreSQL / SCH-M in SQL Server), blocking all concurrent reads and writes.";

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = severity,
            Category = isNarrowing ? RiskCategory.DataLoss : Category,
            Title = $"Altering column '{col}' type on '{table}' to '{newType}' risks table rewrite lock{(isNarrowing ? " & truncation" : "")}",
            Description = detail,
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Use a shadow column (add new column, dual-write, batch-backfill, cut over, drop old column) instead of an in-place ALTER COLUMN on production tables.",
            MitigationScript = mitigation,
            EstimatedImpact = "Full table rewrite + exclusive lock blocking traffic + potential truncation failure",
            Operation = operation
        };
    }
}

public sealed class AddNotNullColumnWithoutDefaultRule : IRiskRule
{
    public string RuleId => "MS005";
    public string Name => "Non-Nullable Column Without Default";
    public string Description => "Detects adding a NOT NULL column without a DEFAULT value, which fails on populated tables or causes blocking rewrites.";
    public RiskCategory Category => RiskCategory.TableLock;
    public RiskSeverity DefaultSeverity => RiskSeverity.High;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType != MigrationOperationType.AddColumn)
            yield break;

        if (operation.IsNullable != false || operation.HasDefaultValue)
            yield break;

        var table = operation.QualifiedTableName;
        var col = operation.ColumnName ?? "<unknown>";
        var colType = operation.DataType ?? "INT";

        var mitigation = document.Dialect == DatabaseDialect.PostgreSql
            ? $"""
               -- Safe 3-step rollout for adding NOT NULL column "{col}" on "{table}":
               -- Step 1: Add column as NULL (fast metadata-only operation):
               ALTER TABLE {table} ADD COLUMN "{col}" {colType} NULL;
               -- Step 2: Backfill existing rows in batches:
               -- UPDATE {table} SET "{col}" = <default_value> WHERE "{col}" IS NULL;
               -- Step 3: Enforce NOT NULL without long exclusive lock (PG 12+):
               ALTER TABLE {table} ADD CONSTRAINT chk_{operation.TableName ?? "tbl"}_{col}_not_null CHECK ("{col}" IS NOT NULL) NOT VALID;
               ALTER TABLE {table} VALIDATE CONSTRAINT chk_{operation.TableName ?? "tbl"}_{col}_not_null;
               ALTER TABLE {table} ALTER COLUMN "{col}" SET NOT NULL;
               ALTER TABLE {table} DROP CONSTRAINT chk_{operation.TableName ?? "tbl"}_{col}_not_null;
               """
            : $"""
               -- Safe rollout for adding NOT NULL column [{col}] on [{table}]:
               -- Option A (SQL Server 2012+ metadata-only with constant default):
               ALTER TABLE {table} ADD [{col}] {colType} NOT NULL CONSTRAINT DF_{operation.TableName ?? "Tbl"}_{col} DEFAULT (<constant_default>);
               -- Option B (Zero-default-value / computed backfill):
               -- 1. ALTER TABLE {table} ADD [{col}] {colType} NULL;
               -- 2. Backfill in batches of 5,000 rows, then ALTER COLUMN [{col}] {colType} NOT NULL.
               """;

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = DefaultSeverity,
            Category = Category,
            Title = $"Adding NOT NULL column '{col}' to '{table}' without a default value",
            Description = $"Adding non-nullable column '{col}' to '{table}' without a DEFAULT constraint immediately fails on any table containing existing rows, and breaks older app versions performing INSERTs that omit '{col}'.",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Add the column as nullable first (or supply a constant metadata-only DEFAULT), backfill existing rows in batches, deploy writers, and enforce NOT NULL afterward.",
            MitigationScript = mitigation,
            EstimatedImpact = "Migration failure on non-empty tables + INSERT failures from older app instances",
            Operation = operation
        };
    }
}

public sealed class BlockingIndexCreationRule : IRiskRule
{
    public string RuleId => "MS007";
    public string Name => "Blocking Index Creation";
    public string Description => "Detects CREATE INDEX statements missing CONCURRENTLY (PostgreSQL) or ONLINE = ON (SQL Server).";
    public RiskCategory Category => RiskCategory.TableLock;
    public RiskSeverity DefaultSeverity => RiskSeverity.High;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType != MigrationOperationType.CreateIndex)
            yield break;

        if (operation.IsConcurrent || operation.IsOnline)
            yield break;

        var table = operation.QualifiedTableName;
        var indexName = operation.IndexName ?? "IX_Target";
        var uniqueKw = operation.IsUnique ? "UNIQUE " : string.Empty;

        var mitigation = document.Dialect == DatabaseDialect.PostgreSql
            ? $"""
               -- Build index online without blocking writes (must run outside an explicit transaction block):
               -- In EF Core: migrationBuilder.Sql("CREATE {uniqueKw}INDEX CONCURRENTLY IF NOT EXISTS \"{indexName}\" ON {table} (<columns>);", suppressTransaction: true);
               CREATE {uniqueKw}INDEX CONCURRENTLY IF NOT EXISTS "{indexName}" ON {table} (<columns>);
               """
            : $"""
               -- Build index online in SQL Server (Enterprise/Azure SQL) with low-priority lock wait:
               CREATE {uniqueKw}NONCLUSTERED INDEX [{indexName}] ON {table} (<columns>)
               WITH (ONLINE = ON (WAIT_AT_LOW_PRIORITY (MAX_DURATION = 5 MINUTES, ABORT_AFTER_WAIT = SELF)));
               """;

        var dialectAdvice = document.Dialect == DatabaseDialect.PostgreSql
            ? "PostgreSQL CREATE INDEX without CONCURRENTLY acquires a SHARE lock that blocks all INSERT/UPDATE/DELETE statements until index build finishes."
            : "SQL Server CREATE INDEX without ONLINE = ON holds a shared/schema lock that blocks concurrent writes (and reads for clustered indexes) for the entire build duration.";

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = DefaultSeverity,
            Category = Category,
            Title = $"Blocking index build '{indexName}' on '{table}' lacks online/concurrent option",
            Description = dialectAdvice,
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = document.Dialect == DatabaseDialect.PostgreSql
                ? "Use CREATE INDEX CONCURRENTLY (with suppressTransaction: true in EF Core)."
                : "Add WITH (ONLINE = ON) and WAIT_AT_LOW_PRIORITY for large tables.",
            MitigationScript = mitigation,
            EstimatedImpact = "Table writes blocked for seconds to hours on large tables",
            Operation = operation
        };
    }
}

public sealed class BlockingIndexDropRule : IRiskRule
{
    public string RuleId => "MS009";
    public string Name => "Blocking Index Drop";
    public string Description => "Detects DROP INDEX without CONCURRENTLY / ONLINE = ON, which acquires an exclusive lock and can regress query plans.";
    public RiskCategory Category => RiskCategory.TableLock;
    public RiskSeverity DefaultSeverity => RiskSeverity.Medium;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType != MigrationOperationType.DropIndex)
            yield break;

        if (operation.IsConcurrent || operation.IsOnline)
            yield break;

        var indexName = operation.IndexName ?? "<unknown>";
        var table = operation.QualifiedTableName;

        var mitigation = document.Dialect == DatabaseDialect.PostgreSql
            ? $"""
               -- Drop index without blocking concurrent reads/writes (suppressTransaction: true in EF Core):
               DROP INDEX CONCURRENTLY IF EXISTS "{indexName}";
               """
            : $"""
               -- Verify index usage via sys.dm_db_index_usage_stats before dropping, and set a lock timeout:
               SET LOCK_TIMEOUT 3000;
               DROP INDEX IF EXISTS [{indexName}] ON {table};
               """;

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = DefaultSeverity,
            Category = Category,
            Title = $"Dropping index '{indexName}' without concurrent/online safeguards",
            Description = $"DROP INDEX '{indexName}' acquires an exclusive lock on '{table}' and may cause immediate query plan regressions (full table scans) if active queries still rely on it.",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Verify index usage statistics first, and use DROP INDEX CONCURRENTLY (PostgreSQL) or a short LOCK_TIMEOUT (SQL Server).",
            MitigationScript = mitigation,
            EstimatedImpact = "Exclusive metadata lock + potential sudden latency spike from full table scans",
            Operation = operation
        };
    }
}

public sealed class AlterColumnSetNotNullRule : IRiskRule
{
    public string RuleId => "MS011";
    public string Name => "Blocking SET NOT NULL Constraint";
    public string Description => "Detects tightening an existing nullable column to NOT NULL, which requires a full table scan under an exclusive lock.";
    public RiskCategory Category => RiskCategory.TableLock;
    public RiskSeverity DefaultSeverity => RiskSeverity.Medium;

    public IEnumerable<RiskFinding> Evaluate(MigrationDocument document, MigrationOperation operation)
    {
        if (operation.OperationType != MigrationOperationType.AlterColumnNullability || operation.IsNullable != false)
            yield break;

        var table = operation.QualifiedTableName;
        var col = operation.ColumnName ?? "<unknown>";

        var mitigation = document.Dialect == DatabaseDialect.PostgreSql
            ? $"""
               -- Avoid ACCESS EXCLUSIVE full table scan on "{table}"."{col}" (PostgreSQL 12+):
               -- 1. Verify no NULL values exist, then add NOT VALID check constraint:
               ALTER TABLE {table} ADD CONSTRAINT chk_{operation.TableName ?? "tbl"}_{col}_nn CHECK ("{col}" IS NOT NULL) NOT VALID;
               -- 2. Validate with SHARE UPDATE EXCLUSIVE lock (allows reads & writes):
               ALTER TABLE {table} VALIDATE CONSTRAINT chk_{operation.TableName ?? "tbl"}_{col}_nn;
               -- 3. Promote to NOT NULL instantaneously (PG skips scan using validated constraint):
               ALTER TABLE {table} ALTER COLUMN "{col}" SET NOT NULL;
               ALTER TABLE {table} DROP CONSTRAINT chk_{operation.TableName ?? "tbl"}_{col}_nn;
               """
            : $"""
               -- Verify zero NULL values exist before enforcing NOT NULL on [{table}].[{col}]:
               -- SELECT TOP 1 1 FROM {table} WHERE [{col}] IS NULL;
               -- Backfill NULL rows in bounded batches first, then apply with LOCK_TIMEOUT:
               SET LOCK_TIMEOUT 3000;
               ALTER TABLE {table} ALTER COLUMN [{col}] {operation.DataType ?? "<type>"} NOT NULL;
               """;

        yield return new RiskFinding
        {
            RuleId = RuleId,
            RuleName = Name,
            Severity = DefaultSeverity,
            Category = Category,
            Title = $"Setting '{table}.{col}' to NOT NULL triggers exclusive full-table scan",
            Description = $"Enforcing NOT NULL directly on '{table}.{col}' scans every row while holding an exclusive lock and fails immediately if any NULL values remain.",
            FilePath = document.FilePath,
            LineNumber = operation.LineNumber,
            CodeSnippet = document.GetSnippetAroundLine(operation.LineNumber),
            Recommendation = "Backfill all NULL values in batches first; in PostgreSQL use a NOT VALID CHECK constraint followed by VALIDATE CONSTRAINT before SET NOT NULL.",
            MitigationScript = mitigation,
            EstimatedImpact = "Full table scan under exclusive lock + deployment failure if NULLs exist",
            Operation = operation
        };
    }
}
