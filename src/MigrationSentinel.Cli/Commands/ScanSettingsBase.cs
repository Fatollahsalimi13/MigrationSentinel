using System.ComponentModel;
using MigrationSentinel.Core.Engine;
using MigrationSentinel.Core.Models;
using MigrationSentinel.Infrastructure.AI;
using MigrationSentinel.Infrastructure.Configuration;
using MigrationSentinel.Infrastructure.Reporting;
using MigrationSentinel.Cli.Rendering;
using Spectre.Console;
using Spectre.Console.Cli;

namespace MigrationSentinel.Cli.Commands;

public abstract class AnalysisCommandSettings : CommandSettings
{
    [CommandOption("-d|--dialect <DIALECT>")]
    [Description("SQL dialect: auto, sqlserver, or postgresql (default: auto).")]
    [DefaultValue("auto")]
    public string Dialect { get; init; } = "auto";

    [CommandOption("--fail-on <SEVERITY>")]
    [Description("Minimum severity that triggers non-zero exit code: none, info, low, medium, high, critical (default: high).")]
    public string? FailOn { get; init; }

    [CommandOption("-f|--format <FORMAT>")]
    [Description("Output format: console, json, markdown, or sarif (default: console).")]
    [DefaultValue("console")]
    public string Format { get; init; } = "console";

    [CommandOption("-o|--output <FILE>")]
    [Description("Optional file path to write the formatted report.")]
    public string? OutputPath { get; init; }

    [CommandOption("--mitigation-out <FILE>")]
    [Description("Optional file path to export consolidated mitigation SQL scripts.")]
    public string? MitigationOutputPath { get; init; }

    [CommandOption("--ai <PROVIDER>")]
    [Description("Optional AI code review provider: none, openai, or ollama (default: none).")]
    public string? AiProvider { get; init; }

    [CommandOption("--ai-model <MODEL>")]
    [Description("Model name for the selected AI provider (e.g., gpt-4o-mini, qwen2.5-coder:7b).")]
    public string? AiModel { get; init; }

    [CommandOption("--ai-endpoint <URL>")]
    [Description("Custom endpoint URL for OpenAI-compatible API or Ollama host.")]
    public string? AiEndpoint { get; init; }

    [CommandOption("--ai-key <API_KEY>")]
    [Description("API key for OpenAI provider (defaults to OPENAI_API_KEY env var).")]
    public string? AiApiKey { get; init; }

    [CommandOption("--exclude-rules <RULE_IDS>")]
    [Description("Comma-separated rule IDs to ignore (e.g., MS013,MS014).")]
    public string? ExcludeRules { get; init; }

    [CommandOption("-v|--verbose")]
    [Description("Show detailed panels for Low and Info findings as well.")]
    public bool Verbose { get; init; }
}

internal static class AnalysisExecutionHelper
{
    public static async Task<int> ExecuteAnalysisAsync(
        IAnsiConsole console,
        string scannedTarget,
        IReadOnlyList<string> files,
        AnalysisCommandSettings settings,
        SentinelConfigFile? config)
    {
        var format = (settings.Format ?? "console").Trim().ToLowerInvariant();
        bool isConsoleMode = format == "console" && string.IsNullOrWhiteSpace(settings.OutputPath);

        var renderer = new ConsoleReportRenderer(console);
        if (isConsoleMode)
            renderer.RenderBanner();

        if (files.Count == 0)
        {
            if (isConsoleMode)
            {
                console.MarkupLine($"[yellow]No SQL or EF Core migration files found at '{Markup.Escape(scannedTarget)}'.[/]");
            }
            else
            {
                var emptyReport = new AnalysisReport
                {
                    ScannedTarget = scannedTarget,
                    Duration = TimeSpan.Zero,
                    Documents = [],
                    Findings = [],
                    RiskScore = 0,
                    OverallSeverity = RiskSeverity.Info
                };
                await EmitFormattedOutputAsync(console, emptyReport, format, settings.OutputPath);
            }
            return 0;
        }

        var options = BuildOptions(settings, config);
        var analyzer = new MigrationAnalyzer();
        AnalysisReport report;

        if (isConsoleMode && console.Profile.Capabilities.Interactive)
        {
            report = await console.Progress()
                .AutoClear(true)
                .Columns(
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new SpinnerColumn())
                .StartAsync(async ctx =>
                {
                    var task = ctx.AddTask($"[cyan]Analyzing {files.Count} migration file(s)...[/]", maxValue: files.Count);
                    return await analyzer.AnalyzeFilesAsync(
                        scannedTarget,
                        files,
                        options,
                        onFileScanned: file =>
                        {
                            task.Description = $"[cyan]Parsing {Markup.Escape(Path.GetFileName(file))}[/]";
                            task.Increment(1);
                        });
                });
        }
        else
        {
            report = await analyzer.AnalyzeFilesAsync(scannedTarget, files, options);
        }

        // Optional AI review layer
        var aiProviderName = !string.IsNullOrWhiteSpace(settings.AiProvider)
            ? settings.AiProvider
            : config?.AiProvider ?? "none";

        var aiOptions = new AiProviderOptions
        {
            Provider = aiProviderName,
            ApiKey = settings.AiApiKey,
            Model = settings.AiModel ?? config?.AiModel,
            Endpoint = settings.AiEndpoint ?? config?.AiEndpoint
        };

        var aiProvider = AiReviewProviderFactory.Create(aiOptions);
        if (aiProvider is not null)
        {
            AiReviewResult aiResult;
            if (isConsoleMode && console.Profile.Capabilities.Interactive)
            {
                aiResult = await console.Status()
                    .Spinner(Spinner.Known.Dots)
                    .StartAsync($"[mediumpurple1]Running {aiProvider.ProviderName} AI migration review...[/]", async _ =>
                        await aiProvider.ReviewAsync(report));
            }
            else
            {
                aiResult = await aiProvider.ReviewAsync(report);
            }

            report = report with { AiReview = aiResult };
        }

        // Render or emit formatted report
        if (format == "console")
        {
            renderer.RenderReport(report, settings.Verbose);
            if (!string.IsNullOrWhiteSpace(settings.OutputPath))
            {
                var md = new MarkdownReportFormatter().Format(report);
                await WriteFileSafelyAsync(settings.OutputPath, md);
                console.MarkupLine($"[green]Saved Markdown report to:[/] {Markup.Escape(settings.OutputPath)}");
            }
        }
        else
        {
            await EmitFormattedOutputAsync(console, report, format, settings.OutputPath);
        }

        // Optional consolidated mitigation SQL export
        if (!string.IsNullOrWhiteSpace(settings.MitigationOutputPath))
        {
            var mitigationSql = MigrationAnalyzer.GenerateConsolidatedMitigationScript(report);
            await WriteFileSafelyAsync(settings.MitigationOutputPath, mitigationSql);
            if (isConsoleMode)
                console.MarkupLine($"[cyan]Exported mitigation SQL script to:[/] {Markup.Escape(settings.MitigationOutputPath)}");
        }

        if (report.ExceedsThreshold(options.FailOnSeverity))
        {
            if (isConsoleMode)
            {
                console.MarkupLine($"[bold red]✗ Gate failed:[/] Found risks at or above threshold [bold]{options.FailOnSeverity}[/].");
            }
            return 1;
        }

        return 0;
    }

    private static SentinelOptions BuildOptions(AnalysisCommandSettings settings, SentinelConfigFile? config)
    {
        var dialect = ParseDialect(settings.Dialect, config?.Dialect ?? DatabaseDialect.Auto);
        var failOn = ParseFailOn(settings.FailOn, config?.FailOn ?? RiskSeverity.High);

        var disabled = new HashSet<string>(config?.DisabledRules ?? [], StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(settings.ExcludeRules))
        {
            foreach (var id in settings.ExcludeRules.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                disabled.Add(id);
        }

        var overrides = new Dictionary<string, RiskSeverity>(
            config?.SeverityOverrides ?? [],
            StringComparer.OrdinalIgnoreCase);

        return new SentinelOptions
        {
            Dialect = dialect,
            FailOnSeverity = failOn,
            DisabledRules = disabled,
            SeverityOverrides = overrides
        };
    }

    private static DatabaseDialect ParseDialect(string? raw, DatabaseDialect fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        return raw.Trim().ToLowerInvariant() switch
        {
            "sqlserver" or "mssql" or "tsql" => DatabaseDialect.SqlServer,
            "postgresql" or "postgres" or "pg" or "pgsql" => DatabaseDialect.PostgreSql,
            "auto" => fallback == DatabaseDialect.Auto ? DatabaseDialect.Auto : fallback,
            _ => fallback
        };
    }

    private static RiskSeverity? ParseFailOn(string? raw, RiskSeverity? fallback)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return fallback;

        return raw.Trim().ToLowerInvariant() switch
        {
            "none" or "off" => null,
            "info" => RiskSeverity.Info,
            "low" => RiskSeverity.Low,
            "medium" => RiskSeverity.Medium,
            "high" => RiskSeverity.High,
            "critical" => RiskSeverity.Critical,
            _ => fallback
        };
    }

    private static async Task EmitFormattedOutputAsync(
        IAnsiConsole console,
        AnalysisReport report,
        string format,
        string? outputPath)
    {
        string content = format switch
        {
            "json" => new JsonReportFormatter().Format(report),
            "markdown" or "md" => new MarkdownReportFormatter().Format(report),
            "sarif" => new SarifReportFormatter().Format(report),
            _ => new JsonReportFormatter().Format(report)
        };

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            await WriteFileSafelyAsync(outputPath, content);
        }
        else
        {
            console.WriteLine(content);
        }
    }

    private static async Task WriteFileSafelyAsync(string path, string content)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(full, content);
    }
}
