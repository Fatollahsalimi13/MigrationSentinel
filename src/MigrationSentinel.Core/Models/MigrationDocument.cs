namespace MigrationSentinel.Core.Models;

/// <summary>
/// Represents a parsed migration source file along with its extracted AST operations.
/// </summary>
public sealed record MigrationDocument
{
    public required string FilePath { get; init; }
    public required string Content { get; init; }
    public required MigrationSourceType SourceType { get; init; }
    public required DatabaseDialect Dialect { get; init; }
    public IReadOnlyList<MigrationOperation> Operations { get; init; } = [];
    public string? MigrationClassName { get; init; }

    public string GetSnippetAroundLine(int lineNumber, int contextLines = 1)
    {
        if (string.IsNullOrEmpty(Content) || lineNumber <= 0)
            return string.Empty;

        var lines = Content.Replace("\r\n", "\n").Split('\n');
        var start = Math.Max(0, lineNumber - 1 - contextLines);
        var end = Math.Min(lines.Length - 1, lineNumber - 1 + contextLines);
        return string.Join(Environment.NewLine, lines[start..(end + 1)]).Trim();
    }
}
