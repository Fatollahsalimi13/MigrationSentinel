namespace MigrationSentinel.Core.Models;

/// <summary>
/// Supported relational database SQL dialects.
/// </summary>
public enum DatabaseDialect
{
    Auto = 0,
    SqlServer = 1,
    PostgreSql = 2
}

/// <summary>
/// Source artifact format for a database migration.
/// </summary>
public enum MigrationSourceType
{
    SqlScript = 0,
    EfCoreMigration = 1
}

/// <summary>
/// Risk severity level assigned to a migration finding.
/// </summary>
public enum RiskSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

/// <summary>
/// Classification of database migration operational risk.
/// </summary>
public enum RiskCategory
{
    DataLoss = 0,
    TableLock = 1,
    BreakingContract = 2,
    Performance = 3,
    Security = 4,
    BestPractice = 5
}

/// <summary>
/// Normalized semantic operation extracted from SQL or EF Core ASTs.
/// </summary>
public enum MigrationOperationType
{
    Unknown = 0,
    DropTable,
    TruncateTable,
    DropColumn,
    AlterColumnType,
    AlterColumnNullability,
    AddColumn,
    RenameTable,
    RenameColumn,
    CreateIndex,
    DropIndex,
    DropPrimaryKey,
    DropForeignKey,
    DropConstraint,
    AddForeignKey,
    UnfilteredDelete,
    UnfilteredUpdate,
    RawSql,
    MissingDownMigration
}
