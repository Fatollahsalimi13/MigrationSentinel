using System.Diagnostics;
using MigrationSentinel.Core.Abstractions;
using MigrationSentinel.Infrastructure.Scanning;

namespace MigrationSentinel.Infrastructure.Git;

/// <summary>
/// Analyzes Git diffs to return only added or modified SQL &amp; EF Core migration files.
/// Ideal for CI/CD Pull Request gating where historical migrations should not block new PRs.
/// </summary>
public sealed class GitDiffMigrationAnalyzer : IGitDiffAnalyzer
{
    public async Task<IReadOnlyList<string>> GetChangedMigrationFilesAsync(
        string repositoryPath,
        string? baseRef = null,
        string? headRef = null,
        bool stagedOnly = false,
        CancellationToken cancellationToken = default)
    {
        var workingDir = Path.GetFullPath(string.IsNullOrWhiteSpace(repositoryPath) ? "." : repositoryPath);
        var repoRoot = (await RunGitAsync(workingDir, ["rev-parse", "--show-toplevel"], cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(repoRoot))
            throw new InvalidOperationException($"Directory '{workingDir}' is not inside a Git repository.");

        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (stagedOnly)
        {
            var output = await RunGitAsync(repoRoot, ["diff", "--cached", "--name-only", "--diff-filter=ACMR"], cancellationToken);
            AddLines(relativePaths, output);
        }
        else if (!string.IsNullOrWhiteSpace(baseRef))
        {
            var targetHead = string.IsNullOrWhiteSpace(headRef) ? "HEAD" : headRef;
            var output = await RunGitAsync(repoRoot, ["diff", "--name-only", "--diff-filter=ACMR", $"{baseRef}...{targetHead}"], cancellationToken);
            AddLines(relativePaths, output);
        }
        else
        {
            // Unstaged + staged changes against HEAD plus untracked files
            var diffOutput = await RunGitAsync(repoRoot, ["diff", "--name-only", "--diff-filter=ACMR", "HEAD"], cancellationToken, allowNonZero: true);
            AddLines(relativePaths, diffOutput);

            var stagedOutput = await RunGitAsync(repoRoot, ["diff", "--cached", "--name-only", "--diff-filter=ACMR"], cancellationToken, allowNonZero: true);
            AddLines(relativePaths, stagedOutput);

            var untrackedOutput = await RunGitAsync(repoRoot, ["ls-files", "--others", "--exclude-standard"], cancellationToken, allowNonZero: true);
            AddLines(relativePaths, untrackedOutput);
        }

        var matched = new List<string>();
        foreach (var relPath in relativePaths)
        {
            var fullPath = Path.GetFullPath(Path.Combine(repoRoot, relPath));
            if (!File.Exists(fullPath))
                continue;

            if (await FileSystemMigrationScanner.IsCandidateMigrationFileAsync(fullPath, cancellationToken))
                matched.Add(fullPath);
        }

        matched.Sort(StringComparer.OrdinalIgnoreCase);
        return matched;
    }

    private static void AddLines(HashSet<string> set, string output)
    {
        foreach (var line in output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(line))
                set.Add(line);
        }
    }

    private static async Task<string> RunGitAsync(
        string workingDirectory,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken,
        bool allowNonZero = false)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var arg in arguments)
            psi.ArgumentList.Add(arg);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start git process. Ensure git is installed and available on PATH.");

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken);

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0 && !allowNonZero)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed (exit {process.ExitCode}): {stderr.Trim()}");
        }

        return stdout;
    }
}
