using FluentAssertions;
using MigrationSentinel.Core.Engine;
using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Tests;

public sealed class SqlParserAndRulesTests
{
    private readonly MigrationAnalyzer _analyzer = new();

    [Fact]
    public void DropTable_SqlServer_ShouldFlagCriticalMS001WithSqlServerMitigation()
    {
        const string sql = """
            DROP TABLE [dbo].[LegacyOrders];
            """;

        var report = _analyzer.AnalyzeContent("001_drop.sql", sql);

        report.Documents.Should().ContainSingle();
        report.Documents[0].Dialect.Should().Be(DatabaseDialect.SqlServer);
        report.Findings.Should().ContainSingle(f => f.RuleId == "MS001");
        report.Findings[0].Severity.Should().Be(RiskSeverity.Critical);
        report.Findings[0].Category.Should().Be(RiskCategory.DataLoss);
        report.Findings[0].LineNumber.Should().Be(1);
        report.Findings[0].MitigationScript.Should().Contain("sp_rename");
    }

    [Fact]
    public void DropColumn_And_SpRename_SqlServer_ShouldFlagMS002AndMS006()
    {
        const string sql = """
            ALTER TABLE [dbo].[Users] DROP COLUMN [MiddleName];
            GO
            EXEC sp_rename 'dbo.Users.EmailAddr', 'Email', 'COLUMN';
            GO
            """;

        var report = _analyzer.AnalyzeContent("002_alter.sql", sql);

        report.Findings.Select(f => f.RuleId).Should().Contain(["MS002", "MS006"]);
        report.Findings.First(f => f.RuleId == "MS002").Severity.Should().Be(RiskSeverity.Critical);
        report.Findings.First(f => f.RuleId == "MS006").LineNumber.Should().Be(3);
    }

    [Fact]
    public void CreateIndex_SqlServer_WithoutOnline_ShouldFlagMS007_WithOnline_ShouldPass()
    {
        const string blockingSql = "CREATE NONCLUSTERED INDEX [IX_Orders_Status] ON [dbo].[Orders] ([Status]);";
        const string onlineSql = "CREATE NONCLUSTERED INDEX [IX_Orders_Status] ON [dbo].[Orders] ([Status]) WITH (ONLINE = ON);";

        var blockingReport = _analyzer.AnalyzeContent("blocking.sql", blockingSql);
        var onlineReport = _analyzer.AnalyzeContent("online.sql", onlineSql);

        blockingReport.Findings.Should().ContainSingle(f => f.RuleId == "MS007");
        blockingReport.Findings[0].MitigationScript.Should().Contain("ONLINE = ON");
        onlineReport.Findings.Should().BeEmpty();
    }

    [Fact]
    public void CreateIndex_PostgreSql_Concurrently_ShouldPass_BlockingShouldFlagMS007()
    {
        const string blockingPg = """
            CREATE INDEX "idx_events_ts" ON "public"."events" ("created_at");
            """;
        const string concurrentPg = """
            CREATE INDEX CONCURRENTLY IF NOT EXISTS "idx_events_ts" ON "public"."events" ("created_at");
            """;

        var blockingReport = _analyzer.AnalyzeContent("pg_block.sql", blockingPg, new SentinelOptions { Dialect = DatabaseDialect.PostgreSql });
        var concurrentReport = _analyzer.AnalyzeContent("pg_safe.sql", concurrentPg, new SentinelOptions { Dialect = DatabaseDialect.PostgreSql });

        blockingReport.Findings.Should().ContainSingle(f => f.RuleId == "MS007");
        blockingReport.Findings[0].MitigationScript.Should().Contain("CONCURRENTLY");
        concurrentReport.Findings.Should().BeEmpty();
    }

    [Fact]
    public void MultiActionAlterTable_PostgreSql_ShouldParseAllClausesIndependently()
    {
        const string sql = """
            ALTER TABLE "public"."accounts"
                DROP COLUMN "legacy_code",
                ALTER COLUMN "balance" SET DATA TYPE NUMERIC(12, 2),
                ADD COLUMN "tenant_id" UUID NOT NULL,
                ALTER COLUMN "email" SET NOT NULL;
            """;

        var report = _analyzer.AnalyzeContent("pg_multi.sql", sql);

        report.Documents[0].Dialect.Should().Be(DatabaseDialect.PostgreSql);
        report.Findings.Select(f => f.RuleId).Should().Contain(["MS002", "MS004", "MS005", "MS011"]);
    }

    [Fact]
    public void AddColumn_NotNullWithDefaultOrNullable_ShouldNotFlagMS005()
    {
        const string sql = """
            ALTER TABLE "public"."orders" ADD COLUMN "notes" TEXT NULL;
            ALTER TABLE "public"."orders" ADD COLUMN "is_archived" BOOLEAN NOT NULL DEFAULT FALSE;
            """;

        var report = _analyzer.AnalyzeContent("safe_add.sql", sql);

        report.Findings.Should().BeEmpty();
        report.RiskScore.Should().Be(0);
    }

    [Fact]
    public void UnfilteredDeleteAndUpdate_ShouldFlagMS008_WhileFilteredShouldPass()
    {
        const string riskySql = """
            DELETE FROM [dbo].[TempLogs];
            UPDATE [dbo].[Users] SET [IsActive] = 0;
            """;

        const string safeSql = """
            DELETE FROM [dbo].[TempLogs] WHERE [CreatedAt] < '2025-01-01';
            UPDATE [dbo].[Users] SET [IsActive] = 0 WHERE [LastLogin] IS NULL;
            """;

        var riskyReport = _analyzer.AnalyzeContent("risky_dml.sql", riskySql);
        var safeReport = _analyzer.AnalyzeContent("safe_dml.sql", safeSql);

        riskyReport.Findings.Count(f => f.RuleId == "MS008").Should().Be(2);
        safeReport.Findings.Should().BeEmpty();
    }

    [Fact]
    public void TruncateCascade_ShouldFlagMS003AndMS012()
    {
        const string sql = """
            TRUNCATE TABLE "public"."sessions" CASCADE;
            """;

        var report = _analyzer.AnalyzeContent("truncate.sql", sql);

        report.Findings.Select(f => f.RuleId).Should().BeEquivalentTo(["MS003", "MS012"]);
    }

    [Fact]
    public void CommentsAndStringLiterals_ShouldNeverTriggerFalsePositives()
    {
        const string sql = """
            -- DROP TABLE [dbo].[ShouldBeIgnored];
            /*
               /* nested comment */
               TRUNCATE TABLE "public"."also_ignored";
               ALTER TABLE "public"."users" DROP COLUMN "fake";
            */
            INSERT INTO "public"."audit_log" ("message")
            VALUES ('DROP TABLE users; DELETE FROM orders;');

            DO $$
            BEGIN
                -- Dollar quoted body string literal
                PERFORM 'DROP TABLE secret_table;';
            END $$;
            """;

        var report = _analyzer.AnalyzeContent("comments_strings.sql", sql);

        report.Findings.Should().BeEmpty();
    }
}
