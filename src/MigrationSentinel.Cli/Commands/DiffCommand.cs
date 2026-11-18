using System.ComponentModel;
using MigrationSentinel.Infrastructure.Configuration;
using MigrationSentinel.Infrastructure.Git;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MigrationSentinel.Cli.Commands;

public sealed class DiffCommandSettings : AnalysisCommandSettings
{
    [CommandOption("-p|--path <REPO_PATH>")]
    [Description("Path inside the Git repository (default: current directory).")]
    [DefaultValue(".")]
    public string RepositoryPath { get; init; } = ".";

    [CommandOption("--base <REF>")]
    [Description("Base Git ref to diff against (e.g., origin/main, HEAD~1).")]
    public string? BaseRef { get; init; }

    [CommandOption("--head <REF>")]
    [Description("Head Git ref to compare (default: HEAD).")]
    public string? HeadRef { get; init; }

    [CommandOption("--staged")]
    [Description("Scan only staged migration files (ideal for pre-commit hooks).")]
    public bool StagedOnly { get; init; }
}

public sealed class DiffCommand : AsyncCommand<DiffCommandSettings>
{
    private readonly IAnsiConsole _console;

    public DiffCommand(IAnsiConsole console)
    {
        _console = console;
    }

    public override async Task<int> ExecuteAsync(CommandContext context, DiffCommandSettings settings)
    {
        try
        {
            var diffAnalyzer = new GitDiffMigrationAnalyzer();
            var changedFiles = await diffAnalyzer.GetChangedMigrationFilesAsync(
                settings.RepositoryPath,
                settings.BaseRef,
                settings.HeadRef,
                settings.StagedOnly);

            var label = settings.StagedOnly
                ? "git diff --staged"
                : !string.IsNullOrWhiteSpace(settings.BaseRef)
                    ? $"git diff {settings.BaseRef}...{settings.HeadRef ?? "HEAD"}"
                    : "git working tree";

            var config = SentinelConfigLoader.TryLoad(settings.RepositoryPath);
            return await AnalysisExecutionHelper.ExecuteAnalysisAsync(_console, label, changedFiles, settings, config);
        }
        catch (Exception ex)
        {
            _console.MarkupLine($"[bold red]Git Diff Error:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }
    }
}
