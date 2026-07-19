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
    private static readonly Regex TagPattern = new("^v[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z.-]+)?$", RegexOptions.Compiled);

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
        Validate(candidate, "candidate", errors, allowLegacy: false);
        Validate(installed, "installed", errors, allowLegacy: true);
        Validate(running, "running", errors, allowLegacy: true);

        if (string.IsNullOrWhiteSpace(latestApprovedTag))
            errors.Add(offline
                ? "cached latest approved tag is missing"
                : "latest approved tag is missing");
        else if (candidate is not null && !string.Equals(candidate.Tag, latestApprovedTag, StringComparison.Ordinal))
            errors.Add($"candidate tag {candidate.Tag} does not equal latest approved tag {latestApprovedTag}");

        if (running is not null && installed is not null && !IdentityEquals(running, installed))
            errors.Add("running identity diverges from the installed manifest");

        var direction = installed?.Legacy == true && candidate is not null
            ? ReleaseDirection.Upgrade
            : Classify(installed?.Version, candidate?.Version, installed?.Commit, candidate?.Commit);
        if (direction == ReleaseDirection.SameVersion && installed is not null && candidate is not null
            && !IdentityEquals(installed, candidate))
            direction = ReleaseDirection.Divergence;
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

    /// <summary>
    /// Proves that the dependency identities declared by a candidate manifest
    /// are the identities pinned by that candidate commit. Callers must supply
    /// the three files read from <c>manifest.Commit</c>, never from the mutable
    /// working tree.
    /// </summary>
    public static IReadOnlyList<string> ValidateCandidateDependencyLocks(
        ReleaseManifest manifest,
        string nugetLockJson,
        string npmPackageJson,
        string npmLockJson)
    {
        var errors = new List<string>();
        try
        {
            using var nuget = JsonDocument.Parse(nugetLockJson);
            JsonElement? lockedRunner = null;
            if (nuget.RootElement.TryGetProperty("dependencies", out var frameworks))
            {
                foreach (var framework in frameworks.EnumerateObject())
                {
                    if (framework.Value.TryGetProperty("CodingAgentRunner", out var runner))
                    {
                        lockedRunner = runner;
                        break;
                    }
                }
            }

            if (lockedRunner is null)
            {
                errors.Add("candidate CodingAgentRunner is missing from backend/packages.lock.json");
            }
            else
            {
                var runner = lockedRunner.Value;
                CompareLockedValue(manifest.CodingAgentRunner.Version,
                    ReadString(runner, "resolved"), "candidate CodingAgentRunner version", errors);
                var contentHash = ReadString(runner, "contentHash");
                CompareLockedValue(manifest.CodingAgentRunner.Integrity,
                    string.IsNullOrWhiteSpace(contentHash) ? null : $"sha512-{contentHash}",
                    "candidate CodingAgentRunner integrity", errors);
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"candidate backend/packages.lock.json is invalid: {ex.Message}");
        }

        string? packageSpec = null;
        try
        {
            using var package = JsonDocument.Parse(npmPackageJson);
            if (package.RootElement.TryGetProperty("dependencies", out var dependencies))
                packageSpec = ReadString(dependencies, "coding-agent-chat");
            if (string.IsNullOrWhiteSpace(packageSpec))
                errors.Add("candidate Coding Agent Chat is missing from frontend/package.json");
            else if (packageSpec.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                errors.Add("candidate Coding Agent Chat still resolves from a local file: dist artifact");
            else
                CompareLockedValue(manifest.CodingAgentChat.Version, packageSpec,
                    "candidate Coding Agent Chat package.json version", errors);
        }
        catch (JsonException ex)
        {
            errors.Add($"candidate frontend/package.json is invalid: {ex.Message}");
        }

        try
        {
            using var packageLock = JsonDocument.Parse(npmLockJson);
            if (!packageLock.RootElement.TryGetProperty("packages", out var packages)
                || !packages.TryGetProperty("node_modules/coding-agent-chat", out var lockedChat))
            {
                errors.Add("candidate Coding Agent Chat is missing from frontend/package-lock.json");
            }
            else
            {
                var resolved = ReadString(lockedChat, "resolved");
                if (string.IsNullOrWhiteSpace(resolved)
                    || resolved.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
                    errors.Add("candidate Coding Agent Chat lock entry is not an immutable registry artifact");
                CompareLockedValue(manifest.CodingAgentChat.Version,
                    ReadString(lockedChat, "version"), "candidate Coding Agent Chat locked version", errors);
                CompareLockedValue(manifest.CodingAgentChat.Integrity,
                    ReadString(lockedChat, "integrity"), "candidate Coding Agent Chat integrity", errors);
            }
        }
        catch (JsonException ex)
        {
            errors.Add($"candidate frontend/package-lock.json is invalid: {ex.Message}");
        }

        return errors;
    }

    private static string? ReadString(JsonElement value, string property) =>
        value.TryGetProperty(property, out var field) && field.ValueKind == JsonValueKind.String
            ? field.GetString()
            : null;

    private static void CompareLockedValue(string declared, string? locked, string label, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(locked))
            errors.Add($"{label} is missing from its lockfile");
        else if (!string.Equals(declared, locked, StringComparison.Ordinal))
            errors.Add($"{label} mismatch (manifest={declared}, lock={locked})");
    }

    private static void Validate(ReleaseManifest? manifest, string name, List<string> errors, bool allowLegacy)
    {
        if (manifest is null)
        {
            errors.Add($"{name} build manifest is missing");
            return;
        }
        if (manifest.SchemaVersion != 1) errors.Add($"{name} schemaVersion is unsupported");
        if (manifest.Legacy && allowLegacy)
        {
            if (string.IsNullOrWhiteSpace(manifest.Commit) || manifest.Commit == "unknown")
                errors.Add($"{name} legacy migration commit is missing");
            return;
        }
        if (manifest.Legacy || string.IsNullOrWhiteSpace(manifest.Tag) || manifest.Tag == "untagged")
            errors.Add($"{name} immutable release tag is missing");
        if (manifest.Dirty) errors.Add($"{name} build is dirty");
        if (!string.Equals(manifest.Application, "Agent Studio", StringComparison.Ordinal))
            errors.Add($"{name} application identity is invalid");
        if (manifest.BuiltAt is null) errors.Add($"{name} build time is missing");
        if (!TagPattern.IsMatch(manifest.Tag ?? "")) errors.Add($"{name} release tag is invalid");
        if (!TagMatchesVersion(manifest.Tag, manifest.Version))
            errors.Add($"{name} tag/version mismatch ({manifest.Tag} vs {manifest.Version})");
        ValidateArtifact(manifest.CodingAgentRunner, $"{name} CodingAgentRunner", "CodingAgentRunner", errors);
        ValidateArtifact(manifest.CodingAgentChat, $"{name} Coding Agent Chat", "coding-agent-chat", errors);
        if (!IsIntegrity(manifest.Integrity)) errors.Add($"{name} application integrity is missing or invalid");
    }

    private static void ValidateArtifact(ReleaseArtifact? artifact, string name, string expectedName, List<string> errors)
    {
        if (artifact is null)
        {
            errors.Add($"{name} package identity is missing");
            return;
        }
        if (!string.Equals(artifact.Name, expectedName, StringComparison.Ordinal))
            errors.Add($"{name} package name mismatch ({artifact.Name} vs {expectedName})");
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
