using MigrationSentinel.Cli.Rendering;
using MigrationSentinel.Core.Rules;
using MigrationSentinel.Infrastructure.Configuration;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MigrationSentinel.Cli.Commands;

public sealed class RulesCommand : Command
{
    private readonly IAnsiConsole _console;

    public RulesCommand(IAnsiConsole console)
    {
        _console = console;
    }

    public override int Execute(CommandContext context)
    {
        var renderer = new ConsoleReportRenderer(_console);
        renderer.RenderBanner();

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey37)
            .Expand()
            .AddColumn(new TableColumn("[bold]ID[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]Severity[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]Category[/]"))
            .AddColumn(new TableColumn("[bold]Rule Name[/]"))
            .AddColumn(new TableColumn("[bold]Description[/]"));

        foreach (var rule in RuleCatalog.CreateDefaultRules().OrderBy(r => r.RuleId))
        {
            var (color, badge) = ConsoleReportRenderer.GetSeverityStyle(rule.DefaultSeverity);
            table.AddRow(
                $"[bold cyan]{rule.RuleId}[/]",
                $"[bold {color}]{badge}[/]",
                $"[grey]{rule.Category}[/]",
                $"[white]{Markup.Escape(rule.Name)}[/]",
                Markup.Escape(rule.Description));
        }

        _console.Write(table);
        return 0;
    }
}

public sealed class InitCommand : Command
{
    private readonly IAnsiConsole _console;

    public InitCommand(IAnsiConsole console)
    {
        _console = console;
    }

    public override int Execute(CommandContext context)
    {
        var path = Path.Combine(Directory.GetCurrentDirectory(), SentinelConfigLoader.DefaultConfigFileName);
        if (File.Exists(path))
        {
            _console.MarkupLine($"[yellow]Configuration file already exists:[/] {Markup.Escape(path)}");
            return 0;
        }

        File.WriteAllText(path, SentinelConfigLoader.SerializeTemplate());
        _console.MarkupLine($"[bold green]✓ Created[/] {Markup.Escape(path)}");
        return 0;
    }
}
