using System.Diagnostics;
using System.Net;
using System.Text;
using FluentAssertions;
using MigrationSentinel.Core.Engine;
using MigrationSentinel.Infrastructure.AI;
using MigrationSentinel.Infrastructure.Git;
using MigrationSentinel.Infrastructure.Scanning;

namespace MigrationSentinel.Tests;

public sealed class InfrastructureAndAiTests
{
    [Fact]
    public async Task FileSystemScanner_ShouldFindMigrationsAndIgnoreBuildArtifactsAndDesigners()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"ms-scan-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(Path.Combine(tempDir, "Migrations"));
            Directory.CreateDirectory(Path.Combine(tempDir, "bin", "Debug"));

            await File.WriteAllTextAsync(Path.Combine(tempDir, "Migrations", "001_Init.sql"), "CREATE TABLE t (id INT);");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Migrations", "2026_AddUsers.cs"),
                "public partial class AddUsers : Migration { protected override void Up(MigrationBuilder migrationBuilder) {} }");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Migrations", "2026_AddUsers.Designer.cs"),
                "public partial class AddUsers : Migration { protected override void Up(MigrationBuilder migrationBuilder) {} }");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "Migrations", "AppModelSnapshot.cs"),
                "class AppModelSnapshot : ModelSnapshot {}");
            await File.WriteAllTextAsync(Path.Combine(tempDir, "bin", "Debug", "ignored.sql"), "DROP TABLE x;");

            var scanner = new FileSystemMigrationScanner();
            var files = await scanner.ScanAsync(tempDir);

            files.Select(Path.GetFileName).Should().BeEquivalentTo(["001_Init.sql", "2026_AddUsers.cs"]);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task GitDiffAnalyzer_ShouldDetectOnlyChangedAndAddedMigrations()
    {
        var repoDir = Path.Combine(Path.GetTempPath(), $"ms-git-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(repoDir);
            await RunGitAsync(repoDir, "init");
            await RunGitAsync(repoDir, "config", "user.name", "Test Bot");
            await RunGitAsync(repoDir, "config", "user.email", "test@example.com");

            await File.WriteAllTextAsync(Path.Combine(repoDir, "001_old.sql"), "DROP TABLE [dbo].[Old];");
            await File.WriteAllTextAsync(Path.Combine(repoDir, "README.md"), "# test");
            await RunGitAsync(repoDir, "add", ".");
            await RunGitAsync(repoDir, "commit", "-m", "initial commit");

            // Add a new migration and stage it
            await File.WriteAllTextAsync(Path.Combine(repoDir, "002_new.sql"), "CREATE INDEX IX_Test ON dbo.Users (Id);");
            await File.WriteAllTextAsync(Path.Combine(repoDir, "notes.txt"), "not a migration");
            await RunGitAsync(repoDir, "add", "002_new.sql", "notes.txt");

            var diffAnalyzer = new GitDiffMigrationAnalyzer();
            var stagedFiles = await diffAnalyzer.GetChangedMigrationFilesAsync(repoDir, stagedOnly: true);

            stagedFiles.Select(Path.GetFileName).Should().BeEquivalentTo(["002_new.sql"]);
        }
        finally
        {
            if (Directory.Exists(repoDir))
                DeleteDirectoryForce(repoDir);
        }
    }

    [Fact]
    public async Task OpenAiReviewProvider_ShouldParseStructuredJsonReview()
    {
        var fakePayload = """
            {
              "choices": [
                {
                  "message": {
                    "content": "{\"executiveSummary\":\"High risk migration due to table drop.\",\"additionalRisks\":[\"Verify foreign key dependents.\"],\"rolloutChecklist\":[\"Take snapshot\",\"Deploy code first\"],\"suggestedMitigationSql\":\"-- archive table first\"}"
                  }
                }
              ]
            }
            """;

        var handler = new StubHttpMessageHandler(HttpStatusCode.OK, fakePayload);
        using var http = new HttpClient(handler);

        var provider = new OpenAiReviewProvider(new AiProviderOptions
        {
            Provider = "openai",
            ApiKey = "sk-test-key",
            Model = "gpt-4o-mini"
        }, http);

        var report = new MigrationAnalyzer().AnalyzeContent("drop.sql", "DROP TABLE [dbo].[Payments];");
        var review = await provider.ReviewAsync(report);

        review.Succeeded.Should().BeTrue();
        review.Provider.Should().Be("OpenAI");
        review.ExecutiveSummary.Should().Contain("High risk migration");
        review.AdditionalRisks.Should().ContainSingle();
        review.RolloutChecklist.Should().HaveCount(2);
        review.SuggestedMitigationSql.Should().Contain("archive table");
    }

    [Fact]
    public async Task OllamaReviewProvider_ShouldParseResponseAndHandleErrorsGracefully()
    {
        var okPayload = """
            {
              "message": {
                "content": "```json\n{\"executiveSummary\":\"Local model confirmed blocking index lock.\",\"additionalRisks\":[],\"rolloutChecklist\":[\"Use CONCURRENTLY\"]}\n```"
              }
            }
            """;

        using var okHttp = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.OK, okPayload));
        var okProvider = new OllamaReviewProvider(new AiProviderOptions { Provider = "ollama", Model = "qwen2.5-coder:7b" }, okHttp);
        var report = new MigrationAnalyzer().AnalyzeContent("idx.sql", "CREATE INDEX idx_a ON public.t (a);");

        var okResult = await okProvider.ReviewAsync(report);
        okResult.Succeeded.Should().BeTrue();
        okResult.ExecutiveSummary.Should().Contain("blocking index lock");
        okResult.RolloutChecklist.Should().ContainSingle("Use CONCURRENTLY");

        // Error handling path
        using var errHttp = new HttpClient(new StubHttpMessageHandler(HttpStatusCode.ServiceUnavailable, "offline"));
        var errProvider = new OllamaReviewProvider(new AiProviderOptions { Provider = "ollama" }, errHttp);
        var errResult = await errProvider.ReviewAsync(report);
        errResult.Succeeded.Should().BeFalse();
        errResult.ErrorMessage.Should().Contain("503");
    }

    private sealed class StubHttpMessageHandler(HttpStatusCode statusCode, string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json")
            });
    }

    private static async Task RunGitAsync(string dir, params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var p = Process.Start(psi)!;
        await p.WaitForExitAsync();
        p.ExitCode.Should().Be(0, await p.StandardError.ReadToEndAsync());
    }

    private static void DeleteDirectoryForce(string path)
    {
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(path, recursive: true);
    }
}
