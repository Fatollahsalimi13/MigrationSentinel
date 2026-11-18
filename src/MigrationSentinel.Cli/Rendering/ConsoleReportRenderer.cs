using MigrationSentinel.Core.Models;
using Spectre.Console;

namespace MigrationSentinel.Cli.Rendering;

/// <summary>
/// Renders rich, colorized terminal reports using Spectre.Console tables, panels, and charts.
/// </summary>
public sealed class ConsoleReportRenderer
{
    private readonly IAnsiConsole _console;

    public ConsoleReportRenderer(IAnsiConsole console)
    {
        _console = console;
    }

    public void RenderBanner()
    {
        var panel = new Panel(
            new Markup(
                "[bold cyan]MigrationSentinel[/] [dim]v1.0.0[/]\n" +
                "[grey]Zero-Downtime Database Migration Guard • EF Core • SQL Server • PostgreSQL[/]"))
        {
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(Color.Cyan1),
            Padding = new Padding(2, 0, 2, 0)
        };
        _console.Write(panel);
        _console.WriteLine();
    }

    public void RenderReport(AnalysisReport report, bool verbose = false)
    {
        RenderSummaryPanel(report);

        if (report.Findings.Count == 0)
        {
            var safePanel = new Panel(
                new Markup("[bold green]✓ PASS[/]  No breaking changes, destructive drops, or locking hazards detected across scanned migrations."))
            {
                Border = BoxBorder.Rounded,
                BorderStyle = new Style(Color.Green)
            };
            _console.Write(safePanel);
            return;
        }

        RenderSeverityChart(report);
        RenderFindingsTable(report);
        RenderMitigationDetails(report, verbose);

        if (report.AiReview is not null)
        {
            RenderAiReview(report.AiReview);
        }
    }

    private void RenderSummaryPanel(AnalysisReport report)
    {
        var (color, badge) = GetSeverityStyle(report.OverallSeverity);
        var grid = new Grid()
            .AddColumn(new GridColumn().PadRight(4))
            .AddColumn(new GridColumn().PadRight(4))
            .AddColumn(new GridColumn().PadRight(4))
            .AddColumn(new GridColumn());

        grid.AddRow(
            $"[dim]Target:[/] [white]{Markup.Escape(report.ScannedTarget)}[/]",
            $"[dim]Files:[/] [bold white]{report.Documents.Count}[/]",
            $"[dim]Findings:[/] [bold white]{report.Findings.Count}[/]",
            $"[dim]Risk Score:[/] [bold {color}]{report.RiskScore}/100 ({badge})[/]");

        var panel = new Panel(grid)
        {
            Header = new PanelHeader($" [bold]Scan Summary ({report.Duration.TotalMilliseconds:F0} ms)[/] "),
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse(color)
        };

        _console.Write(panel);
        _console.WriteLine();
    }

    private void RenderSeverityChart(AnalysisReport report)
    {
        var chart = new BreakdownChart()
            .Width(72);

        if (report.CriticalCount > 0) chart.AddItem("Critical", report.CriticalCount, Color.Red);
        if (report.HighCount > 0) chart.AddItem("High", report.HighCount, Color.DarkOrange);
        if (report.MediumCount > 0) chart.AddItem("Medium", report.MediumCount, Color.Yellow);
        if (report.LowCount > 0) chart.AddItem("Low", report.LowCount, Color.DodgerBlue1);
        if (report.InfoCount > 0) chart.AddItem("Info", report.InfoCount, Color.Grey);

        _console.Write(chart);
        _console.WriteLine();
    }

    private void RenderFindingsTable(AnalysisReport report)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey37)
            .Expand()
            .AddColumn(new TableColumn("[bold]Rule[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]Severity[/]").NoWrap())
            .AddColumn(new TableColumn("[bold]Category[/]"))
            .AddColumn(new TableColumn("[bold]Location[/]"))
            .AddColumn(new TableColumn("[bold]Finding[/]"));

        foreach (var finding in report.Findings)
        {
            var (color, badge) = GetSeverityStyle(finding.Severity);
            var location = $"{Path.GetFileName(finding.FilePath)}:{finding.LineNumber}";

            table.AddRow(
                $"[bold cyan]{Markup.Escape(finding.RuleId)}[/]",
                $"[bold {color}]{badge}[/]",
                $"[grey]{finding.Category}[/]",
                $"[white]{Markup.Escape(location)}[/]",
                Markup.Escape(finding.Title));
        }

        _console.Write(table);
        _console.WriteLine();
    }

    private void RenderMitigationDetails(AnalysisReport report, bool verbose)
    {
        var findingsToShow = verbose
            ? report.Findings
            : report.Findings.Where(f => f.Severity >= RiskSeverity.Medium).ToList();

        if (findingsToShow.Count == 0)
            return;

        _console.Write(new Rule("[bold yellow]Risk Details & Mitigation Guidance[/]").LeftJustified());
        _console.WriteLine();

        foreach (var f in findingsToShow)
        {
            var (color, badge) = GetSeverityStyle(f.Severity);
            var body = new List<string>
            {
                $"[bold]Impact:[/] {Markup.Escape(f.EstimatedImpact)}",
                $"[bold]Why:[/] {Markup.Escape(f.Description)}",
                $"[bold green]Recommendation:[/] {Markup.Escape(f.Recommendation)}"
            };

            if (!string.IsNullOrWhiteSpace(f.MitigationScript))
            {
                body.Add("");
                body.Add("[bold cyan]Suggested Safe SQL / Mitigation Pattern:[/]");
                body.Add($"[dim]{Markup.Escape(f.MitigationScript.Trim())}[/]");
            }

            var panel = new Panel(new Markup(string.Join("\n", body)))
            {
                Header = new PanelHeader($" [{color}]{badge}[/] [bold cyan]{Markup.Escape(f.RuleId)}[/] — {Markup.Escape(Path.GetFileName(f.FilePath))}:{f.LineNumber} "),
                Border = BoxBorder.Rounded,
                BorderStyle = Style.Parse(color),
                Expand = true
            };

            _console.Write(panel);
            _console.WriteLine();
        }
    }

    private void RenderAiReview(AiReviewResult ai)
    {
        var borderColor = ai.Succeeded ? Color.MediumPurple1 : Color.Orange3;
        var lines = new List<string>();

        if (!ai.Succeeded)
        {
            lines.Add($"[yellow]{Markup.Escape(ai.ExecutiveSummary)}[/]");
            if (!string.IsNullOrWhiteSpace(ai.ErrorMessage))
                lines.Add($"[dim]Details: {Markup.Escape(ai.ErrorMessage)}[/]");
        }
        else
        {
            lines.Add($"[bold]Executive Summary:[/]\n{Markup.Escape(ai.ExecutiveSummary)}");

            if (ai.AdditionalRisks.Count > 0)
            {
                lines.Add("");
                lines.Add("[bold yellow]Additional Contextual Risks:[/]");
                foreach (var risk in ai.AdditionalRisks)
                    lines.Add($"  • {Markup.Escape(risk)}");
            }

            if (ai.RolloutChecklist.Count > 0)
            {
                lines.Add("");
                lines.Add("[bold green]Recommended Zero-Downtime Rollout Checklist:[/]");
                for (int i = 0; i < ai.RolloutChecklist.Count; i++)
                    lines.Add($"  {i + 1}. {Markup.Escape(ai.RolloutChecklist[i])}");
            }

            if (!string.IsNullOrWhiteSpace(ai.SuggestedMitigationSql))
            {
                lines.Add("");
                lines.Add("[bold cyan]AI Suggested Mitigation SQL:[/]");
                lines.Add($"[dim]{Markup.Escape(ai.SuggestedMitigationSql.Trim())}[/]");
            }
        }

        var panel = new Panel(new Markup(string.Join("\n", lines)))
        {
            Header = new PanelHeader($" [bold mediumpurple1]AI Migration Review ({Markup.Escape(ai.Provider)} / {Markup.Escape(ai.Model)})[/] "),
            Border = BoxBorder.Rounded,
            BorderStyle = new Style(borderColor),
            Expand = true
        };

        _console.Write(panel);
        _console.WriteLine();
    }

    public static (string Color, string Label) GetSeverityStyle(RiskSeverity severity) => severity switch
    {
        RiskSeverity.Critical => ("red", "CRITICAL"),
        RiskSeverity.High => ("darkorange", "HIGH"),
        RiskSeverity.Medium => ("yellow", "MEDIUM"),
        RiskSeverity.Low => ("dodgerblue1", "LOW"),
        _ => ("green", "INFO")
    };
}
