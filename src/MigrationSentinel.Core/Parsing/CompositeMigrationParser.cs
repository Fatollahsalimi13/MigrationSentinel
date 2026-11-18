using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Core.Models;
using MigrationSentinel.Core.Parsing.EfCore;
using MigrationSentinel.Core.Parsing.Sql;

namespace MigrationSentinel.Core.Parsing;

public sealed class CompositeMigrationParser : IMigrationParser
{
    private readonly IReadOnlyList<IMigrationParser> _parsers;

    public CompositeMigrationParser(IEnumerable<IMigrationParser>? parsers = null)
    {
        if (parsers is not null)
        {
            _parsers = parsers.ToList();
        }
        else
        {
            var sqlParser = new SqlMigrationParser();
            _parsers = [new EfCoreMigrationParser(sqlParser), sqlParser];
        }
    }

    public bool CanParse(string filePath, string content) =>
        _parsers.Any(p => p.CanParse(filePath, content));

    public MigrationDocument Parse(string filePath, string content, DatabaseDialect dialectHint = DatabaseDialect.Auto)
    {
        var parser = _parsers.FirstOrDefault(p => p.CanParse(filePath, content));
        if (parser is not null)
            return parser.Parse(filePath, content, dialectHint);

        return new MigrationDocument
        {
            FilePath = filePath,
            Content = content,
            SourceType = MigrationSourceType.SqlScript,
            Dialect = DialectDetector.Detect(content, dialectHint),
            Operations = []
        };
    }
}
