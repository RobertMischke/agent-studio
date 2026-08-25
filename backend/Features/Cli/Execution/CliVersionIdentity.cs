using System.Text.RegularExpressions;

namespace AgentStudio.Cli;

/// <summary>Normalizes vendor-specific <c>--version</c> output for quota attribution and drift logs.</summary>
public static partial class CliVersionIdentity
{
    [GeneratedRegex(@"(?<version>\d+(?:\.\d+){1,3}(?:[-+][0-9A-Za-z.-]+)?)")]
    private static partial Regex VersionRegex();

    public static string? Normalize(string? rawVersion)
    {
        if (string.IsNullOrWhiteSpace(rawVersion)) return null;
        var match = VersionRegex().Match(rawVersion);
        return match.Success ? match.Groups["version"].Value : rawVersion.Trim();
    }

    public static CliVersionObservation Classify(bool available, string? previous, string? current)
    {
        if (!available || string.IsNullOrWhiteSpace(current)) return CliVersionObservation.Unavailable;
        if (string.IsNullOrWhiteSpace(previous)) return CliVersionObservation.FirstSeen;
        return string.Equals(previous, current, StringComparison.OrdinalIgnoreCase)
            ? CliVersionObservation.Unchanged
            : CliVersionObservation.Changed;
    }
}

public enum CliVersionObservation
{
    Unavailable,
    FirstSeen,
    Unchanged,
    Changed,
}
