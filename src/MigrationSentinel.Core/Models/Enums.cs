namespace MigrationSentinel.Core.Models;

public enum DatabaseDialect
{
    Auto = 0,
    SqlServer = 1,
    PostgreSql = 2
}

public enum MigrationSourceType
{
    SqlScript = 0,
    EfCoreMigration = 1
}

public enum RiskSeverity
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4
}

public enum RiskCategory
{
    DataLoss = 0,
    TableLock = 1,
    BreakingContract = 2,
    Performance = 3,
    Security = 4,
    BestPractice = 5
}

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
