namespace MigrationSentinel.Core.Parsing.Sql;

public enum SqlTokenKind
{
    Word,
    QuotedIdentifier,
    StringLiteral,
    Number,
    Symbol,
    Semicolon,
    Comma,
    Dot,
    LeftParen,
    RightParen,
    Equals
}

public sealed record SqlToken(
    SqlTokenKind Kind,
    string Text,
    string NormalizedValue,
    int Line,
    int Column,
    int StartIndex,
    int Length)
{
    public bool IsKeyword(string keyword) =>
        Kind == SqlTokenKind.Word &&
        Text.Equals(keyword, StringComparison.OrdinalIgnoreCase);

    public bool IsIdentifier => Kind is SqlTokenKind.Word or SqlTokenKind.QuotedIdentifier;
}
