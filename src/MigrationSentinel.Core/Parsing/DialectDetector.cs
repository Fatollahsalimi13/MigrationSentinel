using System.Text.RegularExpressions;
using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Core.Parsing;

/// <summary>
/// Infers whether a migration targets SQL Server or PostgreSQL from syntax and provider hints.
/// </summary>
public static partial class DialectDetector
{
    public static DatabaseDialect Detect(string content, DatabaseDialect hint = DatabaseDialect.Auto)
    {
        if (hint != DatabaseDialect.Auto)
            return hint;

        if (string.IsNullOrWhiteSpace(content))
            return DatabaseDialect.SqlServer;

        int pgScore = 0;
        int sqlServerScore = 0;

        // PostgreSQL indicators
        if (PgIndicatorsRegex().IsMatch(content))
            pgScore += PgIndicatorsRegex().Matches(content).Count * 2;
        if (content.Contains("Npgsql", StringComparison.OrdinalIgnoreCase))
            pgScore += 6;
        if (content.Contains("CONCURRENTLY", StringComparison.OrdinalIgnoreCase))
            pgScore += 4;
        if (content.Contains("::", StringComparison.Ordinal))
            pgScore += 2;
        if (PgQuotedSchemaRegex().IsMatch(content))
            pgScore += 2;

        // SQL Server indicators
        if (SqlServerIndicatorsRegex().IsMatch(content))
            sqlServerScore += SqlServerIndicatorsRegex().Matches(content).Count * 2;
        if (content.Contains("SqlServer:", StringComparison.OrdinalIgnoreCase))
            sqlServerScore += 6;
        if (SqlServerBracketRegex().IsMatch(content))
            sqlServerScore += 3;
        if (SqlServerGoBatchRegex().IsMatch(content))
            sqlServerScore += 3;

        return pgScore > sqlServerScore
            ? DatabaseDialect.PostgreSql
            : DatabaseDialect.SqlServer;
    }

    [GeneratedRegex(@"\b(TIMESTAMPTZ|SERIAL|BIGSERIAL|SMALLSERIAL|BYTEA|JSONB|TEXT\s+NOT\s+NULL|CHARACTER\s+VARYING|DOUBLE\s+PRECISION|USING\s+(GIN|GIST|BTREE|HASH)|SET\s+DATA\s+TYPE|SET\s+NOT\s+NULL|DROP\s+NOT\s+NULL|NOT\s+VALID|VALIDATE\s+CONSTRAINT|public)\b", RegexOptions.IgnoreCase)]
    private static partial Regex PgIndicatorsRegex();

    [GeneratedRegex(@"""[a-zA-Z_][a-zA-Z0-9_]*""\.""[a-zA-Z_][a-zA-Z0-9_]*""")]
    private static partial Regex PgQuotedSchemaRegex();

    [GeneratedRegex(@"\b(NVARCHAR|NCHAR|NTEXT|DATETIME2|DATETIMEOFFSET|UNIQUEIDENTIFIER|SMALLDATETIME|MONEY|IDENTITY\s*\(|CLUSTERED|NONCLUSTERED|sp_rename|WITH\s*\(\s*ONLINE\s*=|WITH\s+NOCHECK|dbo)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SqlServerIndicatorsRegex();

    [GeneratedRegex(@"\[[a-zA-Z_][a-zA-Z0-9_\s]*\]")]
    private static partial Regex SqlServerBracketRegex();

    [GeneratedRegex(@"^\s*GO\s*$", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SqlServerGoBatchRegex();
}
