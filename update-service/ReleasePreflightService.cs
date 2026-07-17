using System.Text.Json;

namespace AgentTaskboard.UpdateService;

public sealed class ReleasePreflightService
{
    private readonly IBackendProbe _backend;
    private readonly UpdateServiceOptions _options;

    public ReleasePreflightService(IBackendProbe backend, UpdateServiceOptions options)
    {
        _backend = backend;
        _options = options;
    }

    public async Task<ReleaseComparison> EvaluateAsync(bool allowDowngrade, CancellationToken ct)
    {
        var running = ToManifest(await _backend.ReadRuntimeVersionAsync(ct));
        var installed = ReadFile(Path.Combine(_options.StableCheckoutDir, _options.BuildManifestFile));
        // Migration from a pre-contract installation has no on-disk manifest.
        // The boot-captured runtime identity is the only truthful rollback
        // anchor, so preserve it as the installed identity for this first hop.
        if (installed is null && running?.Legacy == true)
            installed = running;
        var candidate = ReadFile(_options.CandidateManifestFile);
        var approved = File.Exists(_options.ApprovedTagFile)
            ? File.ReadAllText(_options.ApprovedTagFile).Trim()
            : null;
        return StableReleaseContract.Compare(
            running, installed, candidate, approved, _options.ReleaseMetadataOffline, allowDowngrade);
    }

    public static ReleaseManifest? ToManifest(RuntimeVersion? runtime)
    {
        if (runtime is null) return null;
        return new ReleaseManifest(
            1, "Agent Studio", runtime.Tag ?? "untagged", runtime.Version,
            runtime.Commit, runtime.Dirty, runtime.BuiltAt,
            runtime.Integrity ?? "unverified",
            runtime.CodingAgentRunner!, runtime.CodingAgentChat!, runtime.Legacy);
    }

    private static ReleaseManifest? ReadFile(string path) =>
        File.Exists(path) ? ReadJson(File.ReadAllText(path)) : null;

    private static ReleaseManifest? ReadJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return StableReleaseContract.Read(json); }
        catch (JsonException) { return null; }
    }
}
