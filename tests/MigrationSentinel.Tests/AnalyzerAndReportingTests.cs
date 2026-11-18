using System.Text.Json;
using FluentAssertions;
using MigrationSentinel.Core.Engine;
using MigrationSentinel.Core.Models;
using MigrationSentinel.Infrastructure.Reporting;

namespace MigrationSentinel.Tests;

public sealed class AnalyzerAndReportingTests
{
    private readonly MigrationAnalyzer _analyzer = new();

    [Fact]
    public void Options_DisabledRulesAndSeverityOverrides_ShouldBeApplied()
    {
        const string sql = """
            DROP TABLE [dbo].[Temp];
            CREATE INDEX [IX_A] ON [dbo].[Orders] ([Id]);
            """;

        var options = new SentinelOptions
        {
            DisabledRules = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "MS007" },
            SeverityOverrides = new Dictionary<string, RiskSeverity>(StringComparer.OrdinalIgnoreCase)
            {
                ["MS001"] = RiskSeverity.Medium
            }
        };

        var report = _analyzer.AnalyzeContent("custom.sql", sql, options);

        report.Findings.Should().ContainSingle();
        report.Findings[0].RuleId.Should().Be("MS001");
        report.Findings[0].Severity.Should().Be(RiskSeverity.Medium);
        report.ExceedsThreshold(RiskSeverity.High).Should().BeFalse();
        report.ExceedsThreshold(RiskSeverity.Medium).Should().BeTrue();
    }

    [Fact]
    public void ConsolidatedMitigationScript_ShouldIncludeDialectSpecificRemediation()
    {
        const string pgSql = """
            CREATE INDEX "idx_users_email" ON "public"."users" ("email");
            ALTER TABLE "public"."users" DROP COLUMN "legacy_flag";
            """;

        var report = _analyzer.AnalyzeContent("pg.sql", pgSql);
        var script = MigrationAnalyzer.GenerateConsolidatedMitigationScript(report);

        script.Should().Contain("MS002");
        script.Should().Contain("MS007");
        script.Should().Contain("CREATE INDEX CONCURRENTLY");
    }

    [Fact]
    public void Formatters_JsonMarkdownSarif_ShouldProduceValidOutputs()
    {
        const string sql = "DROP TABLE [dbo].[ArchivedUsers];";
        var report = _analyzer.AnalyzeContent("migrations/001_drop.sql", sql);

        var json = new JsonReportFormatter().Format(report);
        using (var doc = JsonDocument.Parse(json))
        {
            doc.RootElement.GetProperty("tool").GetString().Should().Be("MigrationSentinel");
            doc.RootElement.GetProperty("summary").GetProperty("totalFindings").GetInt32().Should().Be(1);
            doc.RootElement.GetProperty("summary").GetProperty("overallSeverity").GetString().Should().Be("Critical");
        }

        var md = new MarkdownReportFormatter().Format(report);
        md.Should().Contain("# 🛡️ MigrationSentinel Risk Report");
        md.Should().Contain("MS001");
        md.Should().Contain("```sql");

        var sarif = new SarifReportFormatter().Format(report);
        using (var sarifDoc = JsonDocument.Parse(sarif))
        {
            sarifDoc.RootElement.GetProperty("version").GetString().Should().Be("2.1.0");
            var run = sarifDoc.RootElement.GetProperty("runs")[0];
            run.GetProperty("tool").GetProperty("driver").GetProperty("name").GetString().Should().Be("MigrationSentinel");
            run.GetProperty("results").GetArrayLength().Should().Be(1);
            run.GetProperty("results")[0].GetProperty("ruleId").GetString().Should().Be("MS001");
            run.GetProperty("results")[0].GetProperty("level").GetString().Should().Be("error");
        }
    }
}
