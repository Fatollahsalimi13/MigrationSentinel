using System.Text.Json;
using System.Text.Json.Serialization;
using MigrationSentinel.Core.Models;

namespace MigrationSentinel.Infrastructure.Configuration;

public sealed record SentinelConfigFile
{
    public DatabaseDialect Dialect { get; init; } = DatabaseDialect.Auto;
    public RiskSeverity? FailOn { get; init; } = RiskSeverity.High;
    public List<string> DisabledRules { get; init; } = [];
    public Dictionary<string, RiskSeverity> SeverityOverrides { get; init; } = [];
    public string? AiProvider { get; init; }
    public string? AiModel { get; init; }
    public string? AiEndpoint { get; init; }
}

public static class SentinelConfigLoader
{
    public const string DefaultConfigFileName = ".migrationsentinel.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public static SentinelConfigFile? TryLoad(string? startDirectory = null)
    {
        var dir = Path.GetFullPath(string.IsNullOrWhiteSpace(startDirectory) ? Directory.GetCurrentDirectory() : startDirectory);
        if (File.Exists(dir))
            dir = Path.GetDirectoryName(dir) ?? Directory.GetCurrentDirectory();

        var configPath = Path.Combine(dir, DefaultConfigFileName);
        if (!File.Exists(configPath))
            return null;

        var json = File.ReadAllText(configPath);
        return JsonSerializer.Deserialize<SentinelConfigFile>(json, JsonOptions);
    }

    public static string SerializeTemplate() =>
        JsonSerializer.Serialize(new SentinelConfigFile
        {
            Dialect = DatabaseDialect.Auto,
            FailOn = RiskSeverity.High,
            DisabledRules = [],
            SeverityOverrides = new Dictionary<string, RiskSeverity>
            {
                ["MS013"] = RiskSeverity.Info
            },
            AiProvider = "none",
            AiModel = "gpt-4o-mini"
        }, JsonOptions);
}
