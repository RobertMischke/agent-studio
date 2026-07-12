using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentTaskboard.UpdateService;

public sealed record ReleaseArtifact(
    string Name,
    string Version,
    string? Tag,
    string? Commit,
    string Integrity,
    string Source);

public sealed record ReleaseManifest(
    int SchemaVersion,
    string AppTag,
    string AppVersion,
    string Commit,
    bool Dirty,
    DateTime BuiltAt,
    ReleaseArtifact CodingAgentRunner,
    ReleaseArtifact CodingAgentChat);

public enum ReleaseRelation
{
    Upgrade,
    Same,
    Downgrade,
    Diverged,
    Unknown
}

public sealed record ReleasePreflight(
    bool Allowed,
    ReleaseRelation Relation,
    string Message,
    ReleaseManifest? Running,
    ReleaseManifest? Installed,
    ReleaseManifest? Candidate,
    string? LatestApprovedTag);

/// <summary>
/// Pure release gate. It deliberately uses manifests and semantic release tags only;
/// filesystem timestamps are never an input.
/// </summary>
public static class ReleaseContract
{
    private static readonly Regex TagPattern = new("^v(?<v>\\d+\\.\\d+\\.\\d+(?:-[0-9A-Za-z.-]+)?)$", RegexOptions.Compiled);

    public static ReleaseManifest Read(string path)
    {
        var manifest = JsonSerializer.Deserialize<ReleaseManifest>(File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Release manifest is empty.");
        Validate(manifest);
        return manifest;
    }

    public static void Validate(ReleaseManifest manifest)
    {
        if (manifest.SchemaVersion != 1) throw new InvalidDataException($"Unsupported release manifest schema {manifest.SchemaVersion}.");
        var match = TagPattern.Match(manifest.AppTag ?? "");
        if (!match.Success) throw new InvalidDataException("App tag must be an immutable vMAJOR.MINOR.PATCH release tag.");
        if (!string.Equals(match.Groups["v"].Value, manifest.AppVersion, StringComparison.Ordinal))
            throw new InvalidDataException("App tag and version do not match.");
        if (string.IsNullOrWhiteSpace(manifest.Commit)) throw new InvalidDataException("App commit is missing.");
        if (manifest.Dirty) throw new InvalidDataException("Dirty builds cannot be deployed to Stable.");
        ValidateArtifact(manifest.CodingAgentRunner, "CodingAgentRunner");
        ValidateArtifact(manifest.CodingAgentChat, "coding-agent-chat");
    }

    public static ReleasePreflight Compare(ReleaseManifest? running, ReleaseManifest? installed,
        ReleaseManifest? candidate, string? latestApprovedTag, bool allowDowngrade = false)
    {
        if (candidate is null) return Deny(ReleaseRelation.Unknown, "Candidate release manifest is missing.");
        try { Validate(candidate); }
        catch (InvalidDataException ex) { return Deny(ReleaseRelation.Unknown, ex.Message); }

        if (!string.IsNullOrWhiteSpace(latestApprovedTag) && !TryVersion(latestApprovedTag, out _))
            return Deny(ReleaseRelation.Unknown, "Latest approved release tag is invalid.");
        if (!allowDowngrade && !string.IsNullOrWhiteSpace(latestApprovedTag) &&
            !string.Equals(candidate.AppTag, latestApprovedTag, StringComparison.Ordinal))
            return Deny(ReleaseRelation.Diverged,
                $"Candidate {candidate.AppTag} does not match latest approved tag {latestApprovedTag}.");

        var baseline = running ?? installed;
        if (baseline is null)
            return new(true, ReleaseRelation.Upgrade, "Migration from an untagged installation to the first approved manifest.", running, installed, candidate, latestApprovedTag);

        if (running is not null && installed is not null && !SameIdentity(running, installed))
            return new(false, ReleaseRelation.Diverged, "Running and installed release manifests diverge.", running, installed, candidate, latestApprovedTag);

        if (string.Equals(baseline.AppTag, candidate.AppTag, StringComparison.Ordinal))
        {
            if (!SameIdentity(baseline, candidate))
                return new(false, ReleaseRelation.Diverged, "The same tag resolves to different commit or package artifacts.", running, installed, candidate, latestApprovedTag);
            return new(true, ReleaseRelation.Same, "Stable already runs this exact release.", running, installed, candidate, latestApprovedTag);
        }

        if (!TryVersion(baseline.AppTag, out var current) || !TryVersion(candidate.AppTag, out var next))
            return Deny(ReleaseRelation.Unknown, "Release tag is missing or invalid.");
        var relation = next.CompareTo(current) > 0 ? ReleaseRelation.Upgrade : ReleaseRelation.Downgrade;
        var allowed = relation != ReleaseRelation.Downgrade || allowDowngrade;
        return new(allowed, relation, allowed ? $"{relation} to {candidate.AppTag}." : "Downgrade requires explicit rollback authorization.", running, installed, candidate, latestApprovedTag);

        ReleasePreflight Deny(ReleaseRelation relation, string message) =>
            new(false, relation, message, running, installed, candidate, latestApprovedTag);
    }

    public static bool SameIdentity(ReleaseManifest left, ReleaseManifest right) =>
        string.Equals(left.AppTag, right.AppTag, StringComparison.Ordinal) &&
        string.Equals(left.AppVersion, right.AppVersion, StringComparison.Ordinal) &&
        string.Equals(left.Commit, right.Commit, StringComparison.OrdinalIgnoreCase) &&
        SameArtifact(left.CodingAgentRunner, right.CodingAgentRunner) &&
        SameArtifact(left.CodingAgentChat, right.CodingAgentChat);

    public static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return "sha256-" + Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool SameArtifact(ReleaseArtifact x, ReleaseArtifact y) =>
        string.Equals(x.Version, y.Version, StringComparison.Ordinal) &&
        string.Equals(x.Tag, y.Tag, StringComparison.Ordinal) &&
        string.Equals(x.Commit, y.Commit, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(x.Integrity, y.Integrity, StringComparison.OrdinalIgnoreCase);

    private static void ValidateArtifact(ReleaseArtifact artifact, string expectedName)
    {
        if (!string.Equals(artifact.Name, expectedName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Expected {expectedName} artifact, found {artifact.Name}.");
        if (string.IsNullOrWhiteSpace(artifact.Version) || string.IsNullOrWhiteSpace(artifact.Integrity) ||
            !artifact.Integrity.StartsWith("sha", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"{expectedName} version/integrity is missing.");
        if (string.IsNullOrWhiteSpace(artifact.Tag) && string.IsNullOrWhiteSpace(artifact.Commit))
            throw new InvalidDataException($"{expectedName} tag or commit is missing.");
    }

    private static bool TryVersion(string tag, out Version version)
    {
        var match = TagPattern.Match(tag ?? "");
        var core = match.Success ? match.Groups["v"].Value.Split('-', 2)[0] : "";
        return Version.TryParse(core, out version!);
    }
}
