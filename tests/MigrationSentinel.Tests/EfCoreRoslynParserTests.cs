using FluentAssertions;
using MigrationSentinel.Core.Engine;
using MigrationSentinel.Core.Models;
using MigrationSentinel.Core.Parsing.EfCore;

namespace MigrationSentinel.Tests;

public sealed class EfCoreRoslynParserTests
{
    private readonly MigrationAnalyzer _analyzer = new();

    [Fact]
    public void EfCoreMigration_WithDestructiveOpsAndEmptyDown_ShouldDetectAllRules()
    {
        const string cs = """
            using Microsoft.EntityFrameworkCore.Migrations;

            namespace App.Migrations;

            public partial class BreakingSchemaChange : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropTable(
                        name: "OldAuditLogs",
                        schema: "dbo");

                    migrationBuilder.DropColumn(
                        name: "LegacyToken",
                        table: "Users");

                    migrationBuilder.AlterColumn<string>(
                        name: "PhoneNumber",
                        table: "Users",
                        type: "nvarchar(15)",
                        maxLength: 15,
                        nullable: false,
                        oldClrType: typeof(string),
                        oldType: "nvarchar(50)",
                        oldMaxLength: 50,
                        oldNullable: true);

                    migrationBuilder.AddColumn<string>(
                        name: "TenantCode",
                        table: "Users",
                        type: "nvarchar(20)",
                        nullable: false);

                    migrationBuilder.RenameColumn(
                        name: "FirstName",
                        table: "Users",
                        newName: "GivenName");

                    migrationBuilder.CreateIndex(
                        name: "IX_Users_TenantCode",
                        table: "Users",
                        column: "TenantCode");

                    migrationBuilder.DropPrimaryKey(
                        name: "PK_TempEntries",
                        table: "TempEntries");
                }

                protected override void Down(MigrationBuilder migrationBuilder)
                {
                }
            }
            """;

        var report = _analyzer.AnalyzeContent("20261118_BreakingSchemaChange.cs", cs);

        report.Documents.Should().ContainSingle();
        report.Documents[0].SourceType.Should().Be(MigrationSourceType.EfCoreMigration);
        report.Documents[0].MigrationClassName.Should().Be("BreakingSchemaChange");

        var ruleIds = report.Findings.Select(f => f.RuleId).ToList();
        ruleIds.Should().Contain(["MS001", "MS002", "MS004", "MS005", "MS006", "MS007", "MS010", "MS011", "MS014"]);

        // Narrowing maxLength (50 -> 15) elevates MS004 to Critical DataLoss
        var alterFinding = report.Findings.First(f => f.RuleId == "MS004");
        alterFinding.Severity.Should().Be(RiskSeverity.Critical);
        alterFinding.Category.Should().Be(RiskCategory.DataLoss);
    }

    [Fact]
    public void EfCoreMigration_RawSql_ShouldRecursivelyAnalyzeEmbeddedSqlStatements()
    {
        const string cs = """
            using Microsoft.EntityFrameworkCore.Migrations;

            public partial class RawSqlCleanup : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.Sql("DROP TABLE dbo.StaleCache; TRUNCATE TABLE dbo.SessionData;");
                }

                protected override void Down(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.Sql("-- cannot restore");
                }
            }
            """;

        var report = _analyzer.AnalyzeContent("20261118_RawSqlCleanup.cs", cs);

        report.Findings.Select(f => f.RuleId).Should().Contain(["MS013", "MS001", "MS003"]);
        report.Findings.Should().NotContain(f => f.RuleId == "MS014");
    }

    [Fact]
    public void EfCoreMigration_CreateTableWithCascadeForeignKey_ShouldFlagMS012()
    {
        const string cs = """
            using Microsoft.EntityFrameworkCore.Migrations;

            public partial class AddOrderItems : Migration
            {
                protected override void Up(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.CreateTable(
                        name: "OrderItems",
                        columns: table => new
                        {
                            Id = table.Column<int>(nullable: false),
                            OrderId = table.Column<int>(nullable: false)
                        },
                        constraints: table =>
                        {
                            table.PrimaryKey("PK_OrderItems", x => x.Id);
                            table.ForeignKey(
                                name: "FK_OrderItems_Orders_OrderId",
                                column: x => x.OrderId,
                                principalTable: "Orders",
                                principalColumn: "Id",
                                onDelete: ReferentialAction.Cascade);
                        });
                }

                protected override void Down(MigrationBuilder migrationBuilder)
                {
                    migrationBuilder.DropTable(name: "OrderItems");
                }
            }
            """;

        var report = _analyzer.AnalyzeContent("20261118_AddOrderItems.cs", cs);

        report.Findings.Should().ContainSingle(f => f.RuleId == "MS012");
        // DropTable inside Down() must NOT be flagged as an Up() risk
        report.Findings.Should().NotContain(f => f.RuleId == "MS001");
    }

    [Fact]
    public void EfCoreParser_ShouldIgnoreDesignerAndModelSnapshotFiles()
    {
        var parser = new EfCoreMigrationParser();

        parser.CanParse("20261118_Init.Designer.cs", "class Init : Migration { void Up(MigrationBuilder b) {} }").Should().BeFalse();
        parser.CanParse("AppDbContextModelSnapshot.cs", "class AppDbContextModelSnapshot : ModelSnapshot {}").Should().BeFalse();
        parser.CanParse("RegularService.cs", "public class OrderService {}").Should().BeFalse();
    }
}
