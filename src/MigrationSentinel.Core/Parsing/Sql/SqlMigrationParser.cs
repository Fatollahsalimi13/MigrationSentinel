using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Core.Parsing.Sql;

/// <summary>
/// AST-based SQL migration parser supporting SQL Server (T-SQL) and PostgreSQL DDL/DML statements.
/// Extracts normalized <see cref="MigrationOperation"/> instances with exact source line mapping.
/// </summary>
public sealed class SqlMigrationParser : IMigrationParser
{
    private readonly SqlLexer _lexer = new();

    public bool CanParse(string filePath, string content)
    {
        var ext = Path.GetExtension(filePath);
        return ext.Equals(".sql", StringComparison.OrdinalIgnoreCase);
    }

    public MigrationDocument Parse(string filePath, string content, DatabaseDialect dialectHint = DatabaseDialect.Auto)
    {
        var dialect = DialectDetector.Detect(content, dialectHint);
        var operations = ParseSqlContent(content, lineOffset: 0);

        return new MigrationDocument
        {
            FilePath = filePath,
            Content = content,
            SourceType = MigrationSourceType.SqlScript,
            Dialect = dialect,
            Operations = operations
        };
    }

    /// <summary>
    /// Parses raw SQL statements and shifts reported line numbers by <paramref name="lineOffset"/>.
    /// Used both for standalone .sql files and embedded SQL inside EF Core migrationBuilder.Sql() calls.
    /// </summary>
    public IReadOnlyList<MigrationOperation> ParseSqlContent(string sql, int lineOffset = 0)
    {
        var tokens = _lexer.Tokenize(sql);
        var statements = SplitIntoStatements(tokens);
        var operations = new List<MigrationOperation>();

        foreach (var stmt in statements)
        {
            if (stmt.Count == 0)
                continue;

            var rawText = ExtractRawStatement(sql, stmt);
            var parsed = ParseStatement(stmt, rawText, lineOffset);
            operations.AddRange(parsed);
        }

        return operations;
    }

    private static List<List<SqlToken>> SplitIntoStatements(IReadOnlyList<SqlToken> tokens)
    {
        var statements = new List<List<SqlToken>>();
        var current = new List<SqlToken>();

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];

            if (token.Kind == SqlTokenKind.Semicolon)
            {
                if (current.Count > 0)
                {
                    statements.Add(current);
                    current = [];
                }
                continue;
            }

            // T-SQL batch separator: standalone GO at the start of a new line
            if (token.IsKeyword("GO") &&
                (i == 0 || tokens[i - 1].Line < token.Line) &&
                (i == tokens.Count - 1 || tokens[i + 1].Line > token.Line))
            {
                if (current.Count > 0)
                {
                    statements.Add(current);
                    current = [];
                }
                continue;
            }

            current.Add(token);
        }

        if (current.Count > 0)
            statements.Add(current);

        return statements;
    }

    private static string ExtractRawStatement(string sql, List<SqlToken> stmt)
    {
        int start = stmt[0].StartIndex;
        var last = stmt[^1];
        int end = Math.Min(sql.Length, last.StartIndex + last.Length);
        return sql[start..end].Trim();
    }

    private static IEnumerable<MigrationOperation> ParseStatement(
        List<SqlToken> stmt,
        string rawText,
        int lineOffset)
    {
        var first = stmt[0];
        int line = first.Line + lineOffset;

        if (first.IsKeyword("DROP"))
            return ParseDropStatement(stmt, rawText, line);

        if (first.IsKeyword("TRUNCATE"))
            return ParseTruncateStatement(stmt, rawText, line);

        if (first.IsKeyword("ALTER"))
            return ParseAlterStatement(stmt, rawText, lineOffset);

        if (first.IsKeyword("CREATE"))
            return ParseCreateStatement(stmt, rawText, line);

        if (first.IsKeyword("EXEC") || first.IsKeyword("EXECUTE"))
            return ParseExecStatement(stmt, rawText, line);

        if (first.IsKeyword("DELETE"))
            return ParseDeleteStatement(stmt, rawText, line);

        if (first.IsKeyword("UPDATE"))
            return ParseUpdateStatement(stmt, rawText, line);

        return [];
    }

    private static IEnumerable<MigrationOperation> ParseDropStatement(List<SqlToken> stmt, string rawText, int line)
    {
        if (stmt.Count < 2)
            yield break;

        bool isCascade = stmt.Any(t => t.IsKeyword("CASCADE"));

        // DROP TABLE [IF EXISTS] [schema.]table
        if (stmt[1].IsKeyword("TABLE"))
        {
            int idx = SkipIfExists(stmt, 2);
            var (schema, table, _) = ReadQualifiedName(stmt, idx);
            yield return new MigrationOperation
            {
                OperationType = MigrationOperationType.DropTable,
                SchemaName = schema,
                TableName = table,
                IsCascade = isCascade,
                LineNumber = line,
                RawStatement = rawText
            };
            yield break;
        }

        // DROP INDEX [CONCURRENTLY] [IF EXISTS] [schema.]index [ON [schema.]table] [WITH (ONLINE = ON)]
        if (stmt[1].IsKeyword("INDEX"))
        {
            int idx = 2;
            bool isConcurrent = false;
            if (idx < stmt.Count && stmt[idx].IsKeyword("CONCURRENTLY"))
            {
                isConcurrent = true;
                idx++;
            }

            idx = SkipIfExists(stmt, idx);
            var (schema, indexName, nextIdx) = ReadQualifiedName(stmt, idx);
            string? tableName = null;
            string? tableSchema = schema;

            int onIdx = FindKeywordIndex(stmt, "ON", nextIdx);
            if (onIdx >= 0 && onIdx + 1 < stmt.Count)
            {
                var (tSchema, tName, _) = ReadQualifiedName(stmt, onIdx + 1);
                tableSchema = tSchema ?? schema;
                tableName = tName;
            }

            bool isOnline = HasOnlineOnOption(stmt);

            yield return new MigrationOperation
            {
                OperationType = MigrationOperationType.DropIndex,
                SchemaName = tableSchema,
                TableName = tableName,
                IndexName = indexName,
                IsConcurrent = isConcurrent,
                IsOnline = isOnline,
                IsCascade = isCascade,
                LineNumber = line,
                RawStatement = rawText
            };
        }
    }

    private static IEnumerable<MigrationOperation> ParseTruncateStatement(List<SqlToken> stmt, string rawText, int line)
    {
        int idx = 1;
        if (idx < stmt.Count && stmt[idx].IsKeyword("TABLE"))
            idx++;
        if (idx < stmt.Count && stmt[idx].IsKeyword("ONLY"))
            idx++;

        var (schema, table, _) = ReadQualifiedName(stmt, idx);
        bool isCascade = stmt.Any(t => t.IsKeyword("CASCADE"));

        yield return new MigrationOperation
        {
            OperationType = MigrationOperationType.TruncateTable,
            SchemaName = schema,
            TableName = table,
            IsCascade = isCascade,
            LineNumber = line,
            RawStatement = rawText
        };
    }

    private static IEnumerable<MigrationOperation> ParseAlterStatement(List<SqlToken> stmt, string rawText, int lineOffset)
    {
        if (stmt.Count < 3 || !stmt[1].IsKeyword("TABLE"))
            yield break;

        int idx = SkipIfExists(stmt, 2);
        if (idx < stmt.Count && stmt[idx].IsKeyword("ONLY"))
            idx++;

        var (schema, table, actionStart) = ReadQualifiedName(stmt, idx);
        if (actionStart >= stmt.Count)
            yield break;

        // Split top-level comma-separated ALTER TABLE clauses (PostgreSQL multi-action syntax)
        var clauses = SplitTopLevelClauses(stmt, actionStart);
        foreach (var clause in clauses)
        {
            if (clause.Count == 0)
                continue;

            int clauseLine = clause[0].Line + lineOffset;

            foreach (var op in ParseAlterTableClause(clause, schema, table, rawText, clauseLine))
                yield return op;
        }
    }

    private static IEnumerable<MigrationOperation> ParseAlterTableClause(
        List<SqlToken> clause,
        string? schema,
        string? table,
        string rawText,
        int line)
    {
        var head = clause[0];
        bool isCascade = clause.Any(t => t.IsKeyword("CASCADE"));

        // DROP COLUMN [IF EXISTS] col
        if (head.IsKeyword("DROP") && clause.Count >= 2)
        {
            if (clause[1].IsKeyword("COLUMN"))
            {
                int idx = SkipIfExists(clause, 2);
                var col = idx < clause.Count ? clause[idx].NormalizedValue : null;
                yield return new MigrationOperation
                {
                    OperationType = MigrationOperationType.DropColumn,
                    SchemaName = schema,
                    TableName = table,
                    ColumnName = col,
                    IsCascade = isCascade,
                    LineNumber = line,
                    RawStatement = rawText
                };
                yield break;
            }

            if (clause[1].IsKeyword("CONSTRAINT"))
            {
                int idx = SkipIfExists(clause, 2);
                var constraintName = idx < clause.Count ? clause[idx].NormalizedValue : null;
                var opType = InferDropConstraintType(constraintName);
                yield return new MigrationOperation
                {
                    OperationType = opType,
                    SchemaName = schema,
                    TableName = table,
                    ConstraintName = constraintName,
                    IsCascade = isCascade,
                    LineNumber = line,
                    RawStatement = rawText
                };
                yield break;
            }
        }

        // ALTER [COLUMN] col ...
        if (head.IsKeyword("ALTER") && clause.Count >= 3)
        {
            int idx = 1;
            if (clause[idx].IsKeyword("COLUMN"))
                idx++;

            if (idx >= clause.Count)
                yield break;

            var colName = clause[idx].NormalizedValue;
            idx++;

            // PostgreSQL: ALTER COLUMN col SET NOT NULL
            if (HasKeywordSequence(clause, idx, "SET", "NOT", "NULL"))
            {
                yield return new MigrationOperation
                {
                    OperationType = MigrationOperationType.AlterColumnNullability,
                    SchemaName = schema,
                    TableName = table,
                    ColumnName = colName,
                    IsNullable = false,
                    LineNumber = line,
                    RawStatement = rawText
                };
                yield break;
            }

            // PostgreSQL: ALTER COLUMN col [SET DATA] TYPE <type>
            int typeIdx = FindKeywordIndex(clause, "TYPE", idx);
            if (typeIdx >= 0 && typeIdx + 1 < clause.Count)
            {
                var (dataType, maxLen) = ReadDataType(clause, typeIdx + 1);
                yield return new MigrationOperation
                {
                    OperationType = MigrationOperationType.AlterColumnType,
                    SchemaName = schema,
                    TableName = table,
                    ColumnName = colName,
                    DataType = dataType,
                    MaxLength = maxLen,
                    LineNumber = line,
                    RawStatement = rawText
                };
                yield break;
            }

            // Ignore benign SET DEFAULT / DROP DEFAULT / DROP NOT NULL
            if (idx < clause.Count && (clause[idx].IsKeyword("SET") || clause[idx].IsKeyword("DROP")))
                yield break;

            // SQL Server: ALTER COLUMN col <dataType> [NOT NULL | NULL]
            if (idx < clause.Count && clause[idx].IsIdentifier)
            {
                var (dataType, maxLen) = ReadDataType(clause, idx);
                bool hasNotNull = HasKeywordSequence(clause, idx, "NOT", "NULL");

                yield return new MigrationOperation
                {
                    OperationType = MigrationOperationType.AlterColumnType,
                    SchemaName = schema,
                    TableName = table,
                    ColumnName = colName,
                    DataType = dataType,
                    MaxLength = maxLen,
                    IsNullable = hasNotNull ? false : null,
                    LineNumber = line,
                    RawStatement = rawText
                };

                if (hasNotNull)
                {
                    yield return new MigrationOperation
                    {
                        OperationType = MigrationOperationType.AlterColumnNullability,
                        SchemaName = schema,
                        TableName = table,
                        ColumnName = colName,
                        DataType = dataType,
                        IsNullable = false,
                        LineNumber = line,
                        RawStatement = rawText
                    };
                }
                yield break;
            }
        }

        // ADD ...
        if (head.IsKeyword("ADD") && clause.Count >= 2)
        {
            int idx = 1;

            // Check if ADD [CONSTRAINT name] FOREIGN KEY ... ON DELETE CASCADE
            if (clause[idx].IsKeyword("CONSTRAINT") ||
                clause[idx].IsKeyword("FOREIGN") ||
                clause[idx].IsKeyword("PRIMARY") ||
                clause[idx].IsKeyword("UNIQUE") ||
                clause[idx].IsKeyword("CHECK"))
            {
                bool isFk = clause.Any(t => t.IsKeyword("FOREIGN"));
                bool onDeleteCascade = HasKeywordSequence(clause, 0, "ON", "DELETE", "CASCADE");
                if (isFk)
                {
                    string? constraintName = clause[idx].IsKeyword("CONSTRAINT") && idx + 1 < clause.Count
                        ? clause[idx + 1].NormalizedValue
                        : null;
                    yield return new MigrationOperation
                    {
                        OperationType = MigrationOperationType.AddForeignKey,
                        SchemaName = schema,
                        TableName = table,
                        ConstraintName = constraintName,
                        IsCascade = onDeleteCascade,
                        LineNumber = line,
                        RawStatement = rawText
                    };
                }
                yield break;
            }

            // ADD [COLUMN] [IF NOT EXISTS] col <type> [NOT NULL] [DEFAULT ...]
            if (clause[idx].IsKeyword("COLUMN"))
                idx++;
            idx = SkipIfNotExists(clause, idx);

            if (idx < clause.Count && clause[idx].IsIdentifier)
            {
                var colName = clause[idx].NormalizedValue;
                idx++;
                var (dataType, maxLen) = idx < clause.Count ? ReadDataType(clause, idx) : (null, null);
                bool isNotNull = HasKeywordSequence(clause, idx, "NOT", "NULL");
                bool hasDefault = clause.Skip(idx).Any(t => t.IsKeyword("DEFAULT")) ||
                                  clause.Skip(idx).Any(t => t.IsKeyword("IDENTITY")) ||
                                  clause.Skip(idx).Any(t => t.IsKeyword("GENERATED"));

                yield return new MigrationOperation
                {
                    OperationType = MigrationOperationType.AddColumn,
                    SchemaName = schema,
                    TableName = table,
                    ColumnName = colName,
                    DataType = dataType,
                    MaxLength = maxLen,
                    IsNullable = !isNotNull,
                    HasDefaultValue = hasDefault,
                    LineNumber = line,
                    RawStatement = rawText
                };
            }
            yield break;
        }

        // RENAME ...
        if (head.IsKeyword("RENAME") && clause.Count >= 3)
        {
            // RENAME TO new_table
            if (clause[1].IsKeyword("TO"))
            {
                yield return new MigrationOperation
                {
                    OperationType = MigrationOperationType.RenameTable,
                    SchemaName = schema,
                    TableName = table,
                    NewName = clause[2].NormalizedValue,
                    LineNumber = line,
                    RawStatement = rawText
                };
                yield break;
            }

            // RENAME [COLUMN] old_col TO new_col
            int idx = clause[1].IsKeyword("COLUMN") ? 2 : 1;
            int toIdx = FindKeywordIndex(clause, "TO", idx + 1);
            if (toIdx > idx && toIdx + 1 < clause.Count)
            {
                yield return new MigrationOperation
                {
                    OperationType = MigrationOperationType.RenameColumn,
                    SchemaName = schema,
                    TableName = table,
                    ColumnName = clause[idx].NormalizedValue,
                    NewName = clause[toIdx + 1].NormalizedValue,
                    LineNumber = line,
                    RawStatement = rawText
                };
            }
        }
    }

    private static IEnumerable<MigrationOperation> ParseCreateStatement(List<SqlToken> stmt, string rawText, int line)
    {
        // CREATE [UNIQUE] [CLUSTERED|NONCLUSTERED] INDEX [CONCURRENTLY] [IF NOT EXISTS] name ON table (...)
        int indexKeywordPos = -1;
        for (int i = 1; i < Math.Min(stmt.Count, 5); i++)
        {
            if (stmt[i].IsKeyword("INDEX"))
            {
                indexKeywordPos = i;
                break;
            }
            if (stmt[i].IsKeyword("TABLE") || stmt[i].IsKeyword("VIEW") || stmt[i].IsKeyword("PROCEDURE") || stmt[i].IsKeyword("FUNCTION"))
                break;
        }

        if (indexKeywordPos > 0)
        {
            bool isUnique = stmt.Take(indexKeywordPos).Any(t => t.IsKeyword("UNIQUE"));
            int idx = indexKeywordPos + 1;
            bool isConcurrent = false;
            if (idx < stmt.Count && stmt[idx].IsKeyword("CONCURRENTLY"))
            {
                isConcurrent = true;
                idx++;
            }

            idx = SkipIfNotExists(stmt, idx);

            string? indexName = null;
            if (idx < stmt.Count && !stmt[idx].IsKeyword("ON"))
            {
                indexName = stmt[idx].NormalizedValue;
                idx++;
            }

            string? schema = null;
            string? table = null;
            int onIdx = FindKeywordIndex(stmt, "ON", idx);
            if (onIdx >= 0 && onIdx + 1 < stmt.Count)
            {
                int tableStart = onIdx + 1;
                if (stmt[tableStart].IsKeyword("ONLY") && tableStart + 1 < stmt.Count)
                    tableStart++;
                (schema, table, _) = ReadQualifiedName(stmt, tableStart);
            }

            bool isOnline = HasOnlineOnOption(stmt);

            yield return new MigrationOperation
            {
                OperationType = MigrationOperationType.CreateIndex,
                SchemaName = schema,
                TableName = table,
                IndexName = indexName,
                IsUnique = isUnique,
                IsConcurrent = isConcurrent,
                IsOnline = isOnline,
                LineNumber = line,
                RawStatement = rawText
            };
            yield break;
        }

        // CREATE TABLE ... ON DELETE CASCADE detection
        if (stmt.Count >= 3 && stmt[1].IsKeyword("TABLE"))
        {
            int idx = SkipIfNotExists(stmt, 2);
            var (schema, table, _) = ReadQualifiedName(stmt, idx);

            if (HasKeywordSequence(stmt, 0, "ON", "DELETE", "CASCADE"))
            {
                yield return new MigrationOperation
                {
                    OperationType = MigrationOperationType.AddForeignKey,
                    SchemaName = schema,
                    TableName = table,
                    IsCascade = true,
                    LineNumber = line,
                    RawStatement = rawText
                };
            }
        }
    }

    private static IEnumerable<MigrationOperation> ParseExecStatement(List<SqlToken> stmt, string rawText, int line)
    {
        // EXEC [dbo.]sp_rename 'Table.OldCol', 'NewCol', 'COLUMN'
        int renameIdx = stmt.FindIndex(t => t.NormalizedValue.Equals("sp_rename", StringComparison.OrdinalIgnoreCase));
        if (renameIdx < 0)
            yield break;

        var stringArgs = stmt.Skip(renameIdx + 1)
            .Where(t => t.Kind == SqlTokenKind.StringLiteral)
            .Select(t => t.NormalizedValue)
            .ToList();

        if (stringArgs.Count < 2)
            yield break;

        var target = stringArgs[0];
        var newName = stringArgs[1];
        var objectType = stringArgs.Count >= 3 ? stringArgs[2] : string.Empty;

        bool isColumn = objectType.Equals("COLUMN", StringComparison.OrdinalIgnoreCase) ||
                        (string.IsNullOrEmpty(objectType) && target.Count(c => c == '.') >= 2);

        if (isColumn)
        {
            var parts = target.Split('.').Select(p => p.Trim('[', ']', '"')).ToArray();
            yield return new MigrationOperation
            {
                OperationType = MigrationOperationType.RenameColumn,
                SchemaName = parts.Length >= 3 ? parts[0] : null,
                TableName = parts.Length >= 2 ? parts[^2] : target,
                ColumnName = parts.Length >= 2 ? parts[^1] : target,
                NewName = newName,
                LineNumber = line,
                RawStatement = rawText
            };
        }
        else
        {
            var parts = target.Split('.').Select(p => p.Trim('[', ']', '"')).ToArray();
            yield return new MigrationOperation
            {
                OperationType = MigrationOperationType.RenameTable,
                SchemaName = parts.Length >= 2 ? parts[0] : null,
                TableName = parts[^1],
                NewName = newName,
                LineNumber = line,
                RawStatement = rawText
            };
        }
    }

    private static IEnumerable<MigrationOperation> ParseDeleteStatement(List<SqlToken> stmt, string rawText, int line)
    {
        int idx = 1;
        if (idx < stmt.Count && stmt[idx].IsKeyword("FROM"))
            idx++;
        if (idx < stmt.Count && stmt[idx].IsKeyword("ONLY"))
            idx++;

        var (schema, table, _) = ReadQualifiedName(stmt, idx);
        bool hasWhere = HasTopLevelKeyword(stmt, "WHERE");

        if (!hasWhere)
        {
            yield return new MigrationOperation
            {
                OperationType = MigrationOperationType.UnfilteredDelete,
                SchemaName = schema,
                TableName = table,
                HasWhereClause = false,
                LineNumber = line,
                RawStatement = rawText
            };
        }
    }

    private static IEnumerable<MigrationOperation> ParseUpdateStatement(List<SqlToken> stmt, string rawText, int line)
    {
        int idx = 1;
        if (idx < stmt.Count && stmt[idx].IsKeyword("ONLY"))
            idx++;

        var (schema, table, _) = ReadQualifiedName(stmt, idx);
        bool hasWhere = HasTopLevelKeyword(stmt, "WHERE");

        if (!hasWhere)
        {
            yield return new MigrationOperation
            {
                OperationType = MigrationOperationType.UnfilteredUpdate,
                SchemaName = schema,
                TableName = table,
                HasWhereClause = false,
                LineNumber = line,
                RawStatement = rawText
            };
        }
    }

    private static List<List<SqlToken>> SplitTopLevelClauses(List<SqlToken> stmt, int startIndex)
    {
        var clauses = new List<List<SqlToken>>();
        var current = new List<SqlToken>();
        int depth = 0;

        for (int i = startIndex; i < stmt.Count; i++)
        {
            var token = stmt[i];
            if (token.Kind == SqlTokenKind.LeftParen) depth++;
            else if (token.Kind == SqlTokenKind.RightParen && depth > 0) depth--;

            if (token.Kind == SqlTokenKind.Comma && depth == 0)
            {
                if (current.Count > 0)
                {
                    clauses.Add(current);
                    current = [];
                }
                continue;
            }

            current.Add(token);
        }

        if (current.Count > 0)
            clauses.Add(current);

        return clauses;
    }

    private static (string? Schema, string? Name, int NextIndex) ReadQualifiedName(List<SqlToken> tokens, int startIndex)
    {
        if (startIndex >= tokens.Count)
            return (null, null, startIndex);

        var first = tokens[startIndex].NormalizedValue;
        if (startIndex + 2 < tokens.Count && tokens[startIndex + 1].Kind == SqlTokenKind.Dot)
        {
            var second = tokens[startIndex + 2].NormalizedValue;
            return (first, second, startIndex + 3);
        }

        return (null, first, startIndex + 1);
    }

    private static (string DataType, int? MaxLength) ReadDataType(List<SqlToken> tokens, int startIndex)
    {
        var typeName = tokens[startIndex].NormalizedValue;
        int idx = startIndex + 1;

        // Handle multi-word PostgreSQL types like "CHARACTER VARYING", "DOUBLE PRECISION", "TIMESTAMP WITH TIME ZONE"
        if (idx < tokens.Count &&
            ((typeName.Equals("CHARACTER", StringComparison.OrdinalIgnoreCase) && tokens[idx].IsKeyword("VARYING")) ||
             (typeName.Equals("DOUBLE", StringComparison.OrdinalIgnoreCase) && tokens[idx].IsKeyword("PRECISION"))))
        {
            typeName = $"{typeName} {tokens[idx].NormalizedValue}";
            idx++;
        }

        int? maxLength = null;
        if (idx < tokens.Count && tokens[idx].Kind == SqlTokenKind.LeftParen)
        {
            var args = new List<string>();
            int close = idx + 1;
            while (close < tokens.Count && tokens[close].Kind != SqlTokenKind.RightParen)
            {
                args.Add(tokens[close].Text);
                close++;
            }

            var joined = string.Join("", args);
            typeName = $"{typeName}({joined})";

            if (args.Count > 0 && int.TryParse(args[0], out int parsedLen))
                maxLength = parsedLen;
        }

        return (typeName, maxLength);
    }

    private static int SkipIfExists(List<SqlToken> tokens, int idx)
    {
        if (idx + 1 < tokens.Count && tokens[idx].IsKeyword("IF") && tokens[idx + 1].IsKeyword("EXISTS"))
            return idx + 2;
        return idx;
    }

    private static int SkipIfNotExists(List<SqlToken> tokens, int idx)
    {
        if (idx + 2 < tokens.Count &&
            tokens[idx].IsKeyword("IF") &&
            tokens[idx + 1].IsKeyword("NOT") &&
            tokens[idx + 2].IsKeyword("EXISTS"))
            return idx + 3;
        return idx;
    }

    private static int FindKeywordIndex(List<SqlToken> tokens, string keyword, int startIndex)
    {
        for (int i = startIndex; i < tokens.Count; i++)
        {
            if (tokens[i].IsKeyword(keyword))
                return i;
        }
        return -1;
    }

    private static bool HasKeywordSequence(List<SqlToken> tokens, int startIndex, params string[] keywords)
    {
        for (int i = startIndex; i <= tokens.Count - keywords.Length; i++)
        {
            bool match = true;
            for (int k = 0; k < keywords.Length; k++)
            {
                if (!tokens[i + k].IsKeyword(keywords[k]))
                {
                    match = false;
                    break;
                }
            }
            if (match)
                return true;
        }
        return false;
    }

    private static bool HasTopLevelKeyword(List<SqlToken> tokens, string keyword)
    {
        int depth = 0;
        foreach (var token in tokens)
        {
            if (token.Kind == SqlTokenKind.LeftParen) depth++;
            else if (token.Kind == SqlTokenKind.RightParen && depth > 0) depth--;
            else if (depth == 0 && token.IsKeyword(keyword)) return true;
        }
        return false;
    }

    private static bool HasOnlineOnOption(List<SqlToken> tokens)
    {
        for (int i = 0; i < tokens.Count - 2; i++)
        {
            if (tokens[i].IsKeyword("ONLINE") &&
                tokens[i + 1].Kind == SqlTokenKind.Equals &&
                tokens[i + 2].IsKeyword("ON"))
            {
                return true;
            }
        }
        return false;
    }

    private static MigrationOperationType InferDropConstraintType(string? constraintName)
    {
        if (string.IsNullOrWhiteSpace(constraintName))
            return MigrationOperationType.DropConstraint;

        if (constraintName.StartsWith("PK_", StringComparison.OrdinalIgnoreCase) ||
            constraintName.EndsWith("_pkey", StringComparison.OrdinalIgnoreCase))
            return MigrationOperationType.DropPrimaryKey;

        if (constraintName.StartsWith("FK_", StringComparison.OrdinalIgnoreCase) ||
            constraintName.EndsWith("_fkey", StringComparison.OrdinalIgnoreCase))
            return MigrationOperationType.DropForeignKey;

        return MigrationOperationType.DropConstraint;
    }
}
