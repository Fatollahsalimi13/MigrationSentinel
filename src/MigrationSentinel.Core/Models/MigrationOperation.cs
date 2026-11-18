namespace MigrationSentinel.Core.Models;

public sealed record MigrationOperation
{
    public required MigrationOperationType OperationType { get; init; }
    public string? SchemaName { get; init; }
    public string? TableName { get; init; }
    public string? ColumnName { get; init; }
    public string? NewName { get; init; }
    public string? IndexName { get; init; }
    public string? ConstraintName { get; init; }
    public string? DataType { get; init; }
    public string? OldDataType { get; init; }
    public int? MaxLength { get; init; }
    public int? OldMaxLength { get; init; }
    public bool? IsNullable { get; init; }
    public bool? OldIsNullable { get; init; }
    public bool HasDefaultValue { get; init; }
    public bool IsUnique { get; init; }
    public bool IsConcurrent { get; init; }
    public bool IsOnline { get; init; }
    public bool IsCascade { get; init; }
    public bool HasWhereClause { get; init; }
    public int LineNumber { get; init; }
    public string RawStatement { get; init; } = string.Empty;
    public IReadOnlyDictionary<string, string> Metadata { get; init; } = new Dictionary<string, string>();

    public string QualifiedTableName =>
        string.IsNullOrWhiteSpace(SchemaName)
            ? TableName ?? "<unknown>"
            : $"{SchemaName}.{TableName}";
}
