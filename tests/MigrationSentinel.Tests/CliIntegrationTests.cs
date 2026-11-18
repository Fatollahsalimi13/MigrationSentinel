using FluentAssertions;
using MigrationSentinel.Cli;
using Spectre.Console;

namespace MigrationSentinel.Tests;

public sealed class CliIntegrationTests
{
    [Fact]
    public async Task Cli_RulesCommand_ShouldExitZeroAndListBuiltInRules()
    {
        var sw = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,
            Out = new AnsiConsoleOutput(sw)
        });

        var app = CliAppFactory.CreateApp(console);
        var exitCode = await app.RunAsync(["rules"]);

        exitCode.Should().Be(0);
        var output = sw.ToString();
        output.Should().Contain("MS001");
        output.Should().Contain("MS014");
    }

    [Fact]
    public async Task Cli_ScanCommand_ShouldGateRiskyMigrationsAndExportSarifAndMitigation()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ms-cli-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempDir);
            var riskyFile = Path.Combine(tempDir, "risky.sql");
            var safeFile = Path.Combine(tempDir, "safe.sql");
            var sarifOut = Path.Combine(tempDir, "out.sarif");
            var mitigationOut = Path.Combine(tempDir, "mitigation.sql");

            await File.WriteAllTextAsync(riskyFile, "DROP TABLE [dbo].[Invoices];");
            await File.WriteAllTextAsync(safeFile, "CREATE INDEX CONCURRENTLY idx_x ON public.orders (id);");

            var sw = new StringWriter();
            var console = AnsiConsole.Create(new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.No,
                ColorSystem = ColorSystemSupport.NoColors,
                Interactive = InteractionSupport.No,
                Out = new AnsiConsoleOutput(sw)
            });

            var app = CliAppFactory.CreateApp(console);

            // Risky file exceeds default fail-on=high => exit code 1
            var riskyExit = await app.RunAsync(["scan", riskyFile, "--format", "sarif", "--output", sarifOut, "--mitigation-out", mitigationOut]);
            riskyExit.Should().Be(1);
            File.Exists(sarifOut).Should().BeTrue();
            File.Exists(mitigationOut).Should().BeTrue();
            (await File.ReadAllTextAsync(sarifOut)).Should().Contain("MS001");
            (await File.ReadAllTextAsync(mitigationOut)).Should().Contain("sp_rename");

            // Safe file => exit code 0
            var safeExit = await app.RunAsync(["scan", safeFile]);
            safeExit.Should().Be(0);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
