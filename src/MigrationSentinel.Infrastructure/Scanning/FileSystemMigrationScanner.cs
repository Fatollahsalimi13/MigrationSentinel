using MigrationSentinel.Core.Abstractions;

namespace MigrationSentinel.Infrastructure.Scanning;

/// <summary>
/// Recursively discovers SQL migration scripts and Entity Framework Core migration files on disk.
/// </summary>
public sealed class FileSystemMigrationScanner : IMigrationFileScanner
{
    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".vs", ".idea", "node_modules", "packages", "nupkg", "TestResults", "artifacts"
    };

    public async Task<IReadOnlyList<string>> ScanAsync(string targetPath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(targetPath))
            throw new ArgumentException("Target path cannot be empty.", nameof(targetPath));

        var fullPath = Path.GetFullPath(targetPath);

        if (File.Exists(fullPath))
        {
            return await IsCandidateMigrationFileAsync(fullPath, cancellationToken)
                ? [fullPath]
                : [];
        }

        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Target path not found: '{targetPath}'.");

        var results = new List<string>();
        await ScanDirectoryRecursiveAsync(fullPath, results, cancellationToken);
        results.Sort(StringComparer.OrdinalIgnoreCase);
        return results;
    }

    private async Task ScanDirectoryRecursiveAsync(
        string directory,
        List<string> results,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        foreach (var file in Directory.EnumerateFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await IsCandidateMigrationFileAsync(file, cancellationToken))
                results.Add(file);
        }

        foreach (var subDir in Directory.EnumerateDirectories(directory))
        {
            var dirName = Path.GetFileName(subDir);
            if (IgnoredDirectories.Contains(dirName))
                continue;

            await ScanDirectoryRecursiveAsync(subDir, results, cancellationToken);
        }
    }

    public static async Task<bool> IsCandidateMigrationFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        var ext = Path.GetExtension(filePath);

        if (ext.Equals(".sql", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!ext.Equals(".cs", StringComparison.OrdinalIgnoreCase))
            return false;

        var fileName = Path.GetFileName(filePath);
        if (fileName.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase))
            return false;

        // Lightweight read to confirm EF Core Migration signature
        var content = await File.ReadAllTextAsync(filePath, cancellationToken);
        return content.Contains("MigrationBuilder", StringComparison.Ordinal) &&
               (content.Contains(": Migration", StringComparison.Ordinal) ||
                content.Contains("override void Up(", StringComparison.Ordinal));
    }
}
