namespace AgentStudio.DemoReplay;

/// <summary>
/// Startup-only configuration for the public-demo replay ingest scope. There is
/// no management route and no browser toggle: an instance either boots with a
/// pinned trace digest and a verification key or the scope stays closed.
/// </summary>
public sealed record DemoReplayOptions
{
    public const string SectionName = "DemoReplay";

    /// <summary>Default fixture namespaces from ADR-0056. Only these keys can carry a simulated event.</summary>
    public static readonly string[] DefaultTaskKeyPrefixes = ["DEMO-", "PLAT-"];

    public bool Enabled { get; init; }
    public string TraceId { get; init; } = "";
    public string TraceDigest { get; init; } = "";
    public string SigningKeyId { get; init; } = "";
    public string PublicKeyBase64 { get; init; } = "";
    public IReadOnlyList<string> TaskKeyPrefixes { get; init; } = DefaultTaskKeyPrefixes;

    /// <summary>A configured instance is only usable when every pin is present.</summary>
    public bool IsUsable
        => Enabled
           && !string.IsNullOrWhiteSpace(TraceId)
           && !string.IsNullOrWhiteSpace(TraceDigest)
           && !string.IsNullOrWhiteSpace(PublicKeyBase64)
           && TaskKeyPrefixes.Count > 0;

    public static DemoReplayOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(SectionName);
        var prefixes = section.GetSection(nameof(TaskKeyPrefixes)).Get<string[]>();
        return new DemoReplayOptions
        {
            Enabled = section.GetValue("Enabled", false),
            TraceId = (section[nameof(TraceId)] ?? "").Trim(),
            TraceDigest = (section[nameof(TraceDigest)] ?? "").Trim().ToLowerInvariant(),
            SigningKeyId = (section[nameof(SigningKeyId)] ?? "").Trim(),
            PublicKeyBase64 = (section[nameof(PublicKeyBase64)] ?? "").Trim(),
            TaskKeyPrefixes = Normalize(prefixes),
        };
    }

    private static IReadOnlyList<string> Normalize(string[]? prefixes)
    {
        var normalized = (prefixes ?? [])
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Select(prefix => prefix.Trim().ToUpperInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return normalized.Length > 0 ? normalized : DefaultTaskKeyPrefixes;
    }
}
