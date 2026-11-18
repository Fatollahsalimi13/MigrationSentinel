using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Core.Models;
using MigrationSentinel.Core.Parsing.Sql;

namespace MigrationSentinel.Core.Parsing.EfCore;

public sealed class EfCoreMigrationParser : IMigrationParser
{
    private readonly SqlMigrationParser _sqlParser;

    public EfCoreMigrationParser(SqlMigrationParser? sqlParser = null)
    {
        _sqlParser = sqlParser ?? new SqlMigrationParser();
    }

    public bool CanParse(string filePath, string content)
    {
        if (!filePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            return false;

        if (filePath.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ||
            filePath.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            return false;

        return content.Contains("MigrationBuilder", StringComparison.Ordinal) ||
               content.Contains(": Migration", StringComparison.Ordinal);
    }

    public MigrationDocument Parse(string filePath, string content, DatabaseDialect dialectHint = DatabaseDialect.Auto)
    {
        var tree = CSharpSyntaxTree.ParseText(content);
        var root = tree.GetCompilationUnitRoot();
        var dialect = DialectDetector.Detect(content, dialectHint);
        var operations = new List<MigrationOperation>();

        var migrationClass = root.DescendantNodes()
            .OfType<ClassDeclarationSyntax>()
            .FirstOrDefault(IsEfMigrationClass);

        if (migrationClass is null)
        {
            return new MigrationDocument
            {
                FilePath = filePath,
                Content = content,
                SourceType = MigrationSourceType.EfCoreMigration,
                Dialect = dialect,
                Operations = operations
            };
        }

        var upMethod = migrationClass.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text.Equals("Up", StringComparison.Ordinal));

        var downMethod = migrationClass.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.Text.Equals("Down", StringComparison.Ordinal));

        if (upMethod is not null)
        {
            ParseUpMethod(upMethod, operations);
        }

        if (operations.Count > 0 && IsMethodEmptyOrMissing(downMethod))
        {
            int line = downMethod is not null
                ? GetLineNumber(downMethod)
                : GetLineNumber(migrationClass);

            operations.Add(new MigrationOperation
            {
                OperationType = MigrationOperationType.MissingDownMigration,
                TableName = migrationClass.Identifier.Text,
                LineNumber = line,
                RawStatement = downMethod?.ToString().Trim() ?? $"class {migrationClass.Identifier.Text} (no Down() method)"
            });
        }

        return new MigrationDocument
        {
            FilePath = filePath,
            Content = content,
            SourceType = MigrationSourceType.EfCoreMigration,
            Dialect = dialect,
            MigrationClassName = migrationClass.Identifier.Text,
            Operations = operations
        };
    }

    private void ParseUpMethod(MethodDeclarationSyntax upMethod, List<MigrationOperation> operations)
    {
        var builderParamName = upMethod.ParameterList.Parameters.FirstOrDefault()?.Identifier.Text ?? "migrationBuilder";

        foreach (var invocation in upMethod.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (invocation.Expression is not MemberAccessExpressionSyntax memberAccess)
                continue;

            var receiver = memberAccess.Expression.ToString();
            var methodName = memberAccess.Name is GenericNameSyntax genericName
                ? genericName.Identifier.Text
                : memberAccess.Name.Identifier.Text;

            var genericTypeArg = memberAccess.Name is GenericNameSyntax gn
                ? gn.TypeArgumentList.Arguments.FirstOrDefault()?.ToString()
                : null;

            if (!receiver.Equals(builderParamName, StringComparison.Ordinal))
                continue;

            int line = GetLineNumber(invocation);
            var raw = invocation.ToString().Trim();
            var args = ExtractArguments(invocation.ArgumentList);

            switch (methodName)
            {
                case "DropTable":
                    operations.Add(new MigrationOperation
                    {
                        OperationType = MigrationOperationType.DropTable,
                        TableName = GetStringArg(args, "name", 0),
                        SchemaName = GetStringArg(args, "schema", 1),
                        LineNumber = line,
                        RawStatement = raw
                    });
                    break;

                case "DropColumn":
                    operations.Add(new MigrationOperation
                    {
                        OperationType = MigrationOperationType.DropColumn,
                        ColumnName = GetStringArg(args, "name", 0),
                        TableName = GetStringArg(args, "table", 1),
                        SchemaName = GetStringArg(args, "schema", 2),
                        LineNumber = line,
                        RawStatement = raw
                    });
                    break;

                case "AlterColumn":
                    ParseAlterColumn(args, genericTypeArg, line, raw, operations);
                    break;

                case "AddColumn":
                    ParseAddColumn(args, genericTypeArg, line, raw, operations);
                    break;

                case "RenameTable":
                    operations.Add(new MigrationOperation
                    {
                        OperationType = MigrationOperationType.RenameTable,
                        TableName = GetStringArg(args, "name", 0),
                        SchemaName = GetStringArg(args, "schema", 1),
                        NewName = GetStringArg(args, "newName", 2),
                        LineNumber = line,
                        RawStatement = raw
                    });
                    break;

                case "RenameColumn":
                    operations.Add(new MigrationOperation
                    {
                        OperationType = MigrationOperationType.RenameColumn,
                        ColumnName = GetStringArg(args, "name", 0),
                        TableName = GetStringArg(args, "table", 1),
                        NewName = GetStringArg(args, "newName", 2),
                        SchemaName = GetStringArg(args, "schema", 3),
                        LineNumber = line,
                        RawStatement = raw
                    });
                    break;

                case "CreateIndex":
                    operations.Add(new MigrationOperation
                    {
                        OperationType = MigrationOperationType.CreateIndex,
                        IndexName = GetStringArg(args, "name", 0),
                        TableName = GetStringArg(args, "table", 1),
                        SchemaName = GetStringArg(args, "schema", 3),
                        IsUnique = GetBoolArg(args, "unique") ?? false,
                        IsConcurrent = raw.Contains("CONCURRENTLY", StringComparison.OrdinalIgnoreCase),
                        IsOnline = raw.Contains("ONLINE", StringComparison.OrdinalIgnoreCase),
                        LineNumber = line,
                        RawStatement = raw
                    });
                    break;

                case "DropIndex":
                    operations.Add(new MigrationOperation
                    {
                        OperationType = MigrationOperationType.DropIndex,
                        IndexName = GetStringArg(args, "name", 0),
                        TableName = GetStringArg(args, "table", 1),
                        SchemaName = GetStringArg(args, "schema", 2),
                        LineNumber = line,
                        RawStatement = raw
                    });
                    break;

                case "DropPrimaryKey":
                    operations.Add(new MigrationOperation
                    {
                        OperationType = MigrationOperationType.DropPrimaryKey,
                        ConstraintName = GetStringArg(args, "name", 0),
                        TableName = GetStringArg(args, "table", 1),
                        SchemaName = GetStringArg(args, "schema", 2),
                        LineNumber = line,
                        RawStatement = raw
                    });
                    break;

                case "DropForeignKey":
                    operations.Add(new MigrationOperation
                    {
                        OperationType = MigrationOperationType.DropForeignKey,
                        ConstraintName = GetStringArg(args, "name", 0),
                        TableName = GetStringArg(args, "table", 1),
                        SchemaName = GetStringArg(args, "schema", 2),
                        LineNumber = line,
                        RawStatement = raw
                    });
                    break;

                case "AddForeignKey":
                    var fkOnDelete = GetRawArg(args, "onDelete");
                    operations.Add(new MigrationOperation
                    {
                        OperationType = MigrationOperationType.AddForeignKey,
                        ConstraintName = GetStringArg(args, "name", 0),
                        TableName = GetStringArg(args, "table", 1),
                        SchemaName = GetStringArg(args, "schema"),
                        IsCascade = fkOnDelete?.Contains("Cascade", StringComparison.OrdinalIgnoreCase) == true,
                        LineNumber = line,
                        RawStatement = raw
                    });
                    break;

                case "CreateTable":
                    ParseCreateTableCascadeConstraints(invocation, args, operations);
                    break;

                case "Sql":
                    var sqlLiteral = GetStringArg(args, "sql", 0);
                    operations.Add(new MigrationOperation
                    {
                        OperationType = MigrationOperationType.RawSql,
                        LineNumber = line,
                        RawStatement = sqlLiteral ?? raw
                    });

                    if (!string.IsNullOrWhiteSpace(sqlLiteral))
                    {
                        var embeddedOps = _sqlParser.ParseSqlContent(sqlLiteral, lineOffset: line - 1);
                        operations.AddRange(embeddedOps);
                    }
                    break;
            }
        }
    }

    private static void ParseAlterColumn(
        Dictionary<string, ArgumentInfo> args,
        string? genericTypeArg,
        int line,
        string raw,
        List<MigrationOperation> operations)
    {
        var colName = GetStringArg(args, "name", 0);
        var tableName = GetStringArg(args, "table", 1);
        var schema = GetStringArg(args, "schema");
        var newType = GetStringArg(args, "type") ?? genericTypeArg;
        var oldType = GetStringArg(args, "oldType");
        var maxLength = GetIntArg(args, "maxLength");
        var oldMaxLength = GetIntArg(args, "oldMaxLength");
        var nullable = GetBoolArg(args, "nullable");
        var oldNullable = GetBoolArg(args, "oldNullable");

        bool typeChanged = !string.IsNullOrWhiteSpace(newType) &&
                           (string.IsNullOrWhiteSpace(oldType) ||
                            !newType.Equals(oldType, StringComparison.OrdinalIgnoreCase) ||
                            (maxLength.HasValue && (!oldMaxLength.HasValue || maxLength.Value < oldMaxLength.Value)));

        bool tightenedToNotNull = nullable == false && oldNullable != false;

        if (typeChanged)
        {
            operations.Add(new MigrationOperation
            {
                OperationType = MigrationOperationType.AlterColumnType,
                SchemaName = schema,
                TableName = tableName,
                ColumnName = colName,
                DataType = newType,
                OldDataType = oldType,
                MaxLength = maxLength,
                OldMaxLength = oldMaxLength,
                IsNullable = nullable,
                OldIsNullable = oldNullable,
                LineNumber = line,
                RawStatement = raw
            });
        }

        if (tightenedToNotNull)
        {
            operations.Add(new MigrationOperation
            {
                OperationType = MigrationOperationType.AlterColumnNullability,
                SchemaName = schema,
                TableName = tableName,
                ColumnName = colName,
                DataType = newType,
                IsNullable = false,
                OldIsNullable = oldNullable,
                LineNumber = line,
                RawStatement = raw
            });
        }
    }

    private static void ParseAddColumn(
        Dictionary<string, ArgumentInfo> args,
        string? genericTypeArg,
        int line,
        string raw,
        List<MigrationOperation> operations)
    {
        var colName = GetStringArg(args, "name", 0);
        var tableName = GetStringArg(args, "table", 1);
        var schema = GetStringArg(args, "schema");
        var dataType = GetStringArg(args, "type") ?? genericTypeArg;
        var maxLength = GetIntArg(args, "maxLength");
        var nullable = GetBoolArg(args, "nullable") ?? false;
        bool hasDefault = args.ContainsKey("defaultValue") ||
                          args.ContainsKey("defaultValueSql") ||
                          args.ContainsKey("computedColumnSql");

        operations.Add(new MigrationOperation
        {
            OperationType = MigrationOperationType.AddColumn,
            SchemaName = schema,
            TableName = tableName,
            ColumnName = colName,
            DataType = dataType,
            MaxLength = maxLength,
            IsNullable = nullable,
            HasDefaultValue = hasDefault,
            LineNumber = line,
            RawStatement = raw
        });
    }

    private static void ParseCreateTableCascadeConstraints(
        InvocationExpressionSyntax createTableInvocation,
        Dictionary<string, ArgumentInfo> createTableArgs,
        List<MigrationOperation> operations)
    {
        var tableName = GetStringArg(createTableArgs, "name", 0);
        var schema = GetStringArg(createTableArgs, "schema");

        foreach (var nestedCall in createTableInvocation.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (nestedCall.Expression is not MemberAccessExpressionSyntax ma)
                continue;

            if (!ma.Name.Identifier.Text.Equals("ForeignKey", StringComparison.Ordinal))
                continue;

            var fkArgs = ExtractArguments(nestedCall.ArgumentList);
            var onDelete = GetRawArg(fkArgs, "onDelete");
            if (onDelete?.Contains("Cascade", StringComparison.OrdinalIgnoreCase) == true)
            {
                operations.Add(new MigrationOperation
                {
                    OperationType = MigrationOperationType.AddForeignKey,
                    SchemaName = schema,
                    TableName = tableName,
                    ConstraintName = GetStringArg(fkArgs, "name", 0),
                    IsCascade = true,
                    LineNumber = GetLineNumber(nestedCall),
                    RawStatement = nestedCall.ToString().Trim()
                });
            }
        }
    }

    private static bool IsEfMigrationClass(ClassDeclarationSyntax cls)
    {
        if (cls.BaseList is not null)
        {
            foreach (var baseType in cls.BaseList.Types)
            {
                var name = baseType.Type.ToString();
                if (name.Equals("Migration", StringComparison.Ordinal) ||
                    name.EndsWith(".Migration", StringComparison.Ordinal))
                    return true;
            }
        }

        return cls.Members
            .OfType<MethodDeclarationSyntax>()
            .Any(m => m.Identifier.Text == "Up" &&
                      m.ParameterList.Parameters.Any(p => p.Type?.ToString().Contains("MigrationBuilder") == true));
    }

    private static bool IsMethodEmptyOrMissing(MethodDeclarationSyntax? method)
    {
        if (method is null)
            return true;

        if (method.ExpressionBody is not null)
            return false;

        if (method.Body is null)
            return true;

        return method.Body.Statements.Count == 0;
    }

    private static int GetLineNumber(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    private sealed record ArgumentInfo(int Position, string? Name, ExpressionSyntax Expression);

    private static Dictionary<string, ArgumentInfo> ExtractArguments(ArgumentListSyntax argumentList)
    {
        var dict = new Dictionary<string, ArgumentInfo>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < argumentList.Arguments.Count; i++)
        {
            var arg = argumentList.Arguments[i];
            var name = arg.NameColon?.Name.Identifier.Text;
            var info = new ArgumentInfo(i, name, arg.Expression);

            if (!string.IsNullOrEmpty(name))
                dict[name] = info;

            dict[$"__pos_{i}"] = info;
        }
        return dict;
    }

    private static string? GetStringArg(Dictionary<string, ArgumentInfo> args, string name, int? fallbackPosition = null)
    {
        ArgumentInfo? info = null;
        if (!args.TryGetValue(name, out info) && fallbackPosition.HasValue)
        {
            if (args.TryGetValue($"__pos_{fallbackPosition.Value}", out var posInfo) && posInfo.Name is null)
                info = posInfo;
        }

        if (info is null)
            return null;

        return EvaluateStringExpression(info.Expression);
    }

    private static string? EvaluateStringExpression(ExpressionSyntax expr)
    {
        return expr switch
        {
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression) =>
                lit.Token.ValueText,
            BinaryExpressionSyntax bin when bin.IsKind(SyntaxKind.AddExpression) =>
                (EvaluateStringExpression(bin.Left) ?? string.Empty) +
                (EvaluateStringExpression(bin.Right) ?? string.Empty),
            ParenthesizedExpressionSyntax paren =>
                EvaluateStringExpression(paren.Expression),
            _ => null
        };
    }

    private static bool? GetBoolArg(Dictionary<string, ArgumentInfo> args, string name)
    {
        if (!args.TryGetValue(name, out var info))
            return null;

        return info.Expression switch
        {
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.TrueLiteralExpression) => true,
            LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.FalseLiteralExpression) => false,
            _ => null
        };
    }

    private static int? GetIntArg(Dictionary<string, ArgumentInfo> args, string name)
    {
        if (!args.TryGetValue(name, out var info))
            return null;

        if (info.Expression is LiteralExpressionSyntax lit && lit.Token.Value is int intVal)
            return intVal;

        return null;
    }

    private static string? GetRawArg(Dictionary<string, ArgumentInfo> args, string name) =>
        args.TryGetValue(name, out var info) ? info.Expression.ToString() : null;
}
