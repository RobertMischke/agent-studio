using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentTaskboard.UpdateService;

public sealed record ReleaseArtifact(
    string Name,
    string Version,
    string Tag,
    string Commit,
    string Integrity);

public sealed record ReleaseManifest(
    int SchemaVersion,
    string Application,
    string Tag,
    string Version,
    string Commit,
    bool Dirty,
    DateTimeOffset? BuiltAt,
    string Integrity,
    ReleaseArtifact CodingAgentRunner,
    ReleaseArtifact CodingAgentChat,
    bool Legacy = false);

public enum ReleaseDirection
{
    SameVersion,
    Upgrade,
    Downgrade,
    Divergence,
    Unknown
}

public sealed record ReleaseComparison(
    bool Allowed,
    ReleaseDirection Direction,
    string Summary,
    IReadOnlyList<string> Errors,
    ReleaseManifest? Running,
    ReleaseManifest? Installed,
    ReleaseManifest? Candidate,
    string? LatestApprovedTag,
    bool Offline);

/// <summary>
/// Pure Stable release gate. It never uses filesystem timestamps and can run
/// with a cached approved tag when the network is unavailable.
/// </summary>
public static class StableReleaseContract
{
    private static readonly Regex VersionPattern = new("^(?<major>0|[1-9][0-9]*)\\.(?<minor>0|[1-9][0-9]*)\\.(?<patch>0|[1-9][0-9]*)(?:[-+].*)?$", RegexOptions.Compiled);

    public static ReleaseManifest Read(string json)
    {
        var value = JsonSerializer.Deserialize<ReleaseManifest>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return value ?? throw new InvalidDataException("Release manifest is empty.");
    }

    public static ReleaseComparison Compare(
        ReleaseManifest? running,
        ReleaseManifest? installed,
        ReleaseManifest? candidate,
        string? latestApprovedTag,
        bool offline,
        bool allowDowngrade = false)
    {
        var errors = new List<string>();
        Validate(candidate, "candidate", errors, requireClean: true);
        Validate(installed, "installed", errors, requireClean: false);
        Validate(running, "running", errors, requireClean: false);

        if (candidate is not null && !string.IsNullOrWhiteSpace(latestApprovedTag)
            && !string.Equals(candidate.Tag, latestApprovedTag, StringComparison.Ordinal))
            errors.Add($"candidate tag {candidate.Tag} does not equal latest approved tag {latestApprovedTag}");

        if (running is not null && installed is not null && !IdentityEquals(running, installed))
            errors.Add("running identity diverges from the installed manifest");

        var direction = Classify(installed?.Version, candidate?.Version,
            installed?.Commit, candidate?.Commit);
        if (direction == ReleaseDirection.Downgrade && !allowDowngrade)
            errors.Add("downgrade requires explicit approval");
        if (direction == ReleaseDirection.Divergence)
            errors.Add("same version points at different release artifacts");

        return new ReleaseComparison(
            errors.Count == 0,
            direction,
            Summary(direction, offline),
            errors,
            running,
            installed,
            candidate,
            latestApprovedTag,
            offline);
    }

    public static bool IdentityEquals(ReleaseManifest left, ReleaseManifest right) =>
        left == right;

    private static void Validate(ReleaseManifest? manifest, string name, List<string> errors, bool requireClean)
    {
        if (manifest is null)
        {
            errors.Add($"{name} build manifest is missing");
            return;
        }
        if (manifest.SchemaVersion != 1) errors.Add($"{name} schemaVersion is unsupported");
        if (manifest.Legacy || string.IsNullOrWhiteSpace(manifest.Tag) || manifest.Tag == "untagged")
            errors.Add($"{name} immutable release tag is missing");
        if (requireClean && manifest.Dirty) errors.Add($"{name} build is dirty");
        if (!TagMatchesVersion(manifest.Tag, manifest.Version))
            errors.Add($"{name} tag/version mismatch ({manifest.Tag} vs {manifest.Version})");
        ValidateArtifact(manifest.CodingAgentRunner, $"{name} CodingAgentRunner", errors);
        ValidateArtifact(manifest.CodingAgentChat, $"{name} Coding Agent Chat", errors);
        if (!IsIntegrity(manifest.Integrity)) errors.Add($"{name} application integrity is missing or invalid");
    }

    private static void ValidateArtifact(ReleaseArtifact? artifact, string name, List<string> errors)
    {
        if (artifact is null)
        {
            errors.Add($"{name} package identity is missing");
            return;
        }
        if (string.IsNullOrWhiteSpace(artifact.Version) || string.IsNullOrWhiteSpace(artifact.Commit)
            || string.IsNullOrWhiteSpace(artifact.Tag) || artifact.Tag == "untagged")
            errors.Add($"{name} version/tag/commit is incomplete");
        if (!TagMatchesVersion(artifact.Tag, artifact.Version))
            errors.Add($"{name} tag/version mismatch ({artifact.Tag} vs {artifact.Version})");
        if (!IsIntegrity(artifact.Integrity)) errors.Add($"{name} integrity is missing or invalid");
    }

    private static bool TagMatchesVersion(string? tag, string? version)
    {
        if (string.IsNullOrWhiteSpace(tag) || string.IsNullOrWhiteSpace(version)) return false;
        var normalizedTag = tag.StartsWith('v') ? tag[1..] : tag;
        return string.Equals(normalizedTag, version, StringComparison.Ordinal);
    }

    private static bool IsIntegrity(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && (value.StartsWith("sha256-", StringComparison.OrdinalIgnoreCase)
            || value.StartsWith("sha512-", StringComparison.OrdinalIgnoreCase));

    private static ReleaseDirection Classify(string? installed, string? candidate, string? installedCommit, string? candidateCommit)
    {
        if (!TryVersion(installed, out var from) || !TryVersion(candidate, out var to)) return ReleaseDirection.Unknown;
        var cmp = to.CompareTo(from);
        if (cmp > 0) return ReleaseDirection.Upgrade;
        if (cmp < 0) return ReleaseDirection.Downgrade;
        return string.Equals(installedCommit, candidateCommit, StringComparison.OrdinalIgnoreCase)
            ? ReleaseDirection.SameVersion
            : ReleaseDirection.Divergence;
    }

    private static bool TryVersion(string? value, out Version version)
    {
        version = new Version();
        var match = VersionPattern.Match(value ?? "");
        return match.Success && Version.TryParse(
            $"{match.Groups["major"].Value}.{match.Groups["minor"].Value}.{match.Groups["patch"].Value}", out version!);
    }

    private static string Summary(ReleaseDirection direction, bool offline) =>
        $"{direction switch
        {
            ReleaseDirection.SameVersion => "same version",
            ReleaseDirection.Upgrade => "upgrade",
            ReleaseDirection.Downgrade => "downgrade",
            ReleaseDirection.Divergence => "divergence",
            _ => "comparison unavailable"
        }}{(offline ? " (offline, cached approval)" : "")}";
}
