namespace AgentRunner;

public sealed record DurableArtifactPayload(
    string Name,
    string MediaType,
    string ContentBase64,
    string Sha256);

public sealed record DurableCompletionPayload(
    string Outcome,
    string? Summary,
    string? ResultEnvelopeDigest);

public sealed record DurableRunContextPayload(
    string RepositoryId,
    string? RepositoryUrl,
    string? DefaultBranch,
    string BaseSha);

public sealed record DurableTerminalPayload(
    string Outcome,
    string? Reason);

public sealed record ArtifactManifestEntry(
    string Path,
    string Sha256,
    long SizeBytes);

public sealed record DurableArtifactManifest(
    string Digest,
    string Json);
