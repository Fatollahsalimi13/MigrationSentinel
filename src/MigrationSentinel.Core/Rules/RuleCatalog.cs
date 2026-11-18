using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Core.Rules.BuiltIn;

namespace MigrationSentinel.Core.Rules;

/// <summary>
/// Provides the complete catalog of built-in MigrationSentinel AST &amp; heuristic risk rules.
/// </summary>
public static class RuleCatalog
{
    public static IReadOnlyList<IRiskRule> CreateDefaultRules() =>
    [
        new DropTableRule(),
        new DropColumnRule(),
        new TruncateTableRule(),
        new AlterColumnTypeRule(),
        new AddNotNullColumnWithoutDefaultRule(),
        new RenameObjectRule(),
        new BlockingIndexCreationRule(),
        new UnfilteredDmlRule(),
        new BlockingIndexDropRule(),
        new DropConstraintRule(),
        new AlterColumnSetNotNullRule(),
        new CascadeDeleteRule(),
        new RawSqlInEfMigrationRule(),
        new MissingDownMigrationRule()
    ];
}
