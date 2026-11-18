using System.ComponentModel;
using MigrationSentinel.Infrastructure.Configuration;
using MigrationSentinel.Infrastructure.Scanning;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MigrationSentinel.Cli.Commands;

public sealed class ScanCommandSettings : AnalysisCommandSettings
{
    [CommandArgument(0, "[PATH]")]
    [Description("File or directory containing SQL scripts or EF Core C# migrations (defaults to current directory).")]
    public string Path { get; init; } = ".";
}

public sealed class ScanCommand : AsyncCommand<ScanCommandSettings>
{
    private readonly IAnsiConsole _console;

    public ScanCommand(IAnsiConsole console)
    {
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, ScanCommandSettings settings)
    {
        try
        {
            var scanner = new FileSystemMigrationScanner();
            var files = await scanner.ScanAsync(settings.Path);
            var config = SentinelConfigLoader.TryLoad(settings.Path);
            return await AnalysisExecutionHelper.ExecuteAnalysisAsync(_console, settings.Path, files, settings, config);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[bold red]Error:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }
    }
}
