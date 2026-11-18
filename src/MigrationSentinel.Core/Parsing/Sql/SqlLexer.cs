using System.Text;

namespace MigrationSentinel.Core.Parsing.Sql;

/// <summary>
/// Dialect-aware SQL lexer supporting T-SQL and PostgreSQL syntax.
/// Strips single-line (--), nested multi-line (/* */) comments, and tokenizes
/// quoted identifiers ([name], "name", `name`) and string/dollar-quoted literals.
/// </summary>
public sealed class SqlLexer
{
    public IReadOnlyList<SqlToken> Tokenize(string sql)
    {
        var tokens = new List<SqlToken>();
        if (string.IsNullOrEmpty(sql))
            return tokens;

        int i = 0;
        int line = 1;
        int col = 1;
        int len = sql.Length;

        while (i < len)
        {
            char c = sql[i];

            // Whitespace
            if (char.IsWhiteSpace(c))
            {
                if (c == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
                i++;
                continue;
            }

            // Single-line comment: --
            if (c == '-' && i + 1 < len && sql[i + 1] == '-')
            {
                i += 2;
                col += 2;
                while (i < len && sql[i] != '\n')
                {
                    i++;
                    col++;
                }
                continue;
            }

            // Multi-line comment: /* ... */ (supports nesting for PostgreSQL)
            if (c == '/' && i + 1 < len && sql[i + 1] == '*')
            {
                int depth = 1;
                i += 2;
                col += 2;
                while (i < len && depth > 0)
                {
                    if (sql[i] == '/' && i + 1 < len && sql[i + 1] == '*')
                    {
                        depth++;
                        i += 2;
                        col += 2;
                    }
                    else if (sql[i] == '*' && i + 1 < len && sql[i + 1] == '/')
                    {
                        depth--;
                        i += 2;
                        col += 2;
                    }
                    else
                    {
                        if (sql[i] == '\n')
                        {
                            line++;
                            col = 1;
                        }
                        else
                        {
                            col++;
                        }
                        i++;
                    }
                }
                continue;
            }

            // T-SQL Unicode string N'...' or standard string '...'
            if (c == '\'' || ((c == 'N' || c == 'n' || c == 'E' || c == 'e') && i + 1 < len && sql[i + 1] == '\''))
            {
                int startIdx = i;
                int startLine = line;
                int startCol = col;
                var sb = new StringBuilder();

                if (c != '\'')
                {
                    i++;
                    col++;
                }

                // Skip opening quote
                i++;
                col++;

                while (i < len)
                {
                    if (sql[i] == '\'')
                    {
                        if (i + 1 < len && sql[i + 1] == '\'')
                        {
                            sb.Append('\'');
                            i += 2;
                            col += 2;
                            continue;
                        }
                        i++;
                        col++;
                        break;
                    }

                    if (sql[i] == '\n')
                    {
                        line++;
                        col = 1;
                    }
                    else
                    {
                        col++;
                    }
                    sb.Append(sql[i]);
                    i++;
                }

                var raw = sql[startIdx..i];
                tokens.Add(new SqlToken(SqlTokenKind.StringLiteral, raw, sb.ToString(), startLine, startCol, startIdx, i - startIdx));
                continue;
            }

            // PostgreSQL dollar-quoted string: $$...$$ or $tag$...$tag$
            if (c == '$')
            {
                int tagEnd = i + 1;
                while (tagEnd < len && (char.IsLetterOrDigit(sql[tagEnd]) || sql[tagEnd] == '_'))
                    tagEnd++;

                if (tagEnd < len && sql[tagEnd] == '$')
                {
                    string delimiter = sql[i..(tagEnd + 1)];
                    int startIdx = i;
                    int startLine = line;
                    int startCol = col;
                    int bodyStart = tagEnd + 1;
                    int closePos = sql.IndexOf(delimiter, bodyStart, StringComparison.Ordinal);

                    int endIdx = closePos >= 0 ? closePos + delimiter.Length : len;
                    string body = closePos >= 0 ? sql[bodyStart..closePos] : sql[bodyStart..];

                    for (int k = i; k < endIdx; k++)
                    {
                        if (sql[k] == '\n')
                        {
                            line++;
                            col = 1;
                        }
                        else
                        {
                            col++;
                        }
                    }

                    i = endIdx;
                    tokens.Add(new SqlToken(SqlTokenKind.StringLiteral, sql[startIdx..endIdx], body, startLine, startCol, startIdx, endIdx - startIdx));
                    continue;
                }
            }

            // Quoted identifiers: [name], "name", `name`
            if (c is '[' or '"' or '`')
            {
                char closeChar = c == '[' ? ']' : c;
                int startIdx = i;
                int startLine = line;
                int startCol = col;
                var sb = new StringBuilder();

                i++;
                col++;
                while (i < len)
                {
                    if (sql[i] == closeChar)
                    {
                        if (i + 1 < len && sql[i + 1] == closeChar)
                        {
                            sb.Append(closeChar);
                            i += 2;
                            col += 2;
                            continue;
                        }
                        i++;
                        col++;
                        break;
                    }
                    if (sql[i] == '\n')
                    {
                        line++;
                        col = 1;
                    }
                    else
                    {
                        col++;
                    }
                    sb.Append(sql[i]);
                    i++;
                }

                tokens.Add(new SqlToken(SqlTokenKind.QuotedIdentifier, sql[startIdx..i], sb.ToString(), startLine, startCol, startIdx, i - startIdx));
                continue;
            }

            // Numbers
            if (char.IsDigit(c))
            {
                int startIdx = i;
                int startLine = line;
                int startCol = col;
                while (i < len && (char.IsDigit(sql[i]) || sql[i] == '.'))
                {
                    i++;
                    col++;
                }
                var text = sql[startIdx..i];
                tokens.Add(new SqlToken(SqlTokenKind.Number, text, text, startLine, startCol, startIdx, i - startIdx));
                continue;
            }

            // Words / unquoted identifiers / keywords
            if (char.IsLetter(c) || c == '_' || c == '@' || c == '#')
            {
                int startIdx = i;
                int startLine = line;
                int startCol = col;
                while (i < len && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '@' or '#' or '$'))
                {
                    i++;
                    col++;
                }
                var text = sql[startIdx..i];
                tokens.Add(new SqlToken(SqlTokenKind.Word, text, text, startLine, startCol, startIdx, i - startIdx));
                continue;
            }

            // Single-character punctuation / symbols
            int symStart = i;
            int symCol = col;
            i++;
            col++;

            var kind = c switch
            {
                ';' => SqlTokenKind.Semicolon,
                ',' => SqlTokenKind.Comma,
                '.' => SqlTokenKind.Dot,
                '(' => SqlTokenKind.LeftParen,
                ')' => SqlTokenKind.RightParen,
                '=' => SqlTokenKind.Equals,
                _ => SqlTokenKind.Symbol
            };

            var symText = c.ToString();
            tokens.Add(new SqlToken(kind, symText, symText, line, symCol, symStart, 1));
        }

        return tokens;
    }
}
