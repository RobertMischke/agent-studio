using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentStudio.TaskServer.Contracts;

public static class ResultEnvelopeDigest
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string Compute(ImmutableResultEnvelope envelope)
    {
        Validate(envelope);
        var canonical = new ImmutableResultEnvelope(
            envelope.RepositoryId.Trim(),
            envelope.SourceRunAttemptId.Trim(),
            envelope.BaseSha.ToLowerInvariant(),
            envelope.ResultSha.ToLowerInvariant(),
            envelope.ImmutableRemoteRef?.Trim(),
            envelope.SourceBundleDigest?.ToLowerInvariant(),
            envelope.ArtifactManifestDigest.ToLowerInvariant(),
            Sort(envelope.Submodules),
            Sort(envelope.LfsObjects));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(canonical, Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static void Validate(ImmutableResultEnvelope envelope)
    {
        if (string.IsNullOrWhiteSpace(envelope.RepositoryId))
            throw new ArgumentException("Result envelope repositoryId is required.");
        if (string.IsNullOrWhiteSpace(envelope.SourceRunAttemptId))
            throw new ArgumentException("Result envelope sourceRunAttemptId is required.");
        RequireHexIdentity(envelope.BaseSha, "baseSha", 40, 64);
        RequireHexIdentity(envelope.ResultSha, "resultSha", 40, 64);
        RequireHexIdentity(envelope.ArtifactManifestDigest, "artifactManifestDigest", 64);
        var hasRef = !string.IsNullOrWhiteSpace(envelope.ImmutableRemoteRef);
        var hasBundle = !string.IsNullOrWhiteSpace(envelope.SourceBundleDigest);
        if (hasRef == hasBundle)
            throw new ArgumentException("Result envelope requires exactly one immutable remote ref or source bundle digest.");
        if (hasBundle)
            RequireHexIdentity(envelope.SourceBundleDigest!, "sourceBundleDigest", 64);
        ValidateDependencies(envelope.Submodules, "submodule");
        ValidateDependencies(envelope.LfsObjects, "LFS");
    }

    private static IReadOnlyList<ResultDependencyIdentity> Sort(
        IReadOnlyList<ResultDependencyIdentity>? identities)
        => identities?
               .OrderBy(identity => identity.Path, StringComparer.Ordinal)
               .ThenBy(identity => identity.ObjectId, StringComparer.Ordinal)
               .ToArray()
           ?? [];

    private static void ValidateDependencies(
        IReadOnlyList<ResultDependencyIdentity>? identities,
        string kind)
    {
        if (identities is null) return;
        foreach (var identity in identities)
        {
            if (string.IsNullOrWhiteSpace(identity.Path))
                throw new ArgumentException($"Result envelope {kind} path is required.");
            RequireHexIdentity(identity.ObjectId, $"{kind} object identity", 40, 64);
        }
    }

    private static void RequireHexIdentity(string value, string name, params int[] lengths)
    {
        if (value is null
            || !lengths.Contains(value.Length)
            || value.Any(character => !Uri.IsHexDigit(character)))
            throw new ArgumentException(
                $"Result envelope {name} must be a lowercase or uppercase hexadecimal identity of length {string.Join(" or ", lengths)}.");
    }
}
