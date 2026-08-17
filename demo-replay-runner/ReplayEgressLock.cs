namespace AgentStudio.DemoReplayRunner;

/// <summary>
/// In-process counterpart to the default-deny network policy on the demo VM. The
/// service can address exactly one origin and exactly two paths: the replay
/// ingest and the health probe. Anything else fails before a socket is opened,
/// so a bug or an injected URL cannot turn this process into a general client.
/// </summary>
public sealed class ReplayEgressLock : DelegatingHandler
{
    public const string ReplayPath = "/api/runner/replay/events";
    public const string HealthPath = "/healthz";

    public static readonly string[] AllowedPaths = [ReplayPath, HealthPath];

    private readonly Uri _origin;

    public ReplayEgressLock(Uri origin, HttpMessageHandler inner)
        : base(inner)
    {
        ArgumentNullException.ThrowIfNull(origin);
        _origin = origin;
    }

    public bool Allows(Uri? candidate)
        => candidate is not null
           && candidate.IsAbsoluteUri
           && string.Equals(candidate.Scheme, _origin.Scheme, StringComparison.OrdinalIgnoreCase)
           && string.Equals(candidate.Host, _origin.Host, StringComparison.OrdinalIgnoreCase)
           && candidate.Port == _origin.Port
           && AllowedPaths.Contains(candidate.AbsolutePath, StringComparer.Ordinal);

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        if (!Allows(request.RequestUri))
            throw new InvalidOperationException(
                $"Replay egress lock refused '{request.RequestUri}'. This service may only reach {_origin} on {string.Join(" or ", AllowedPaths)}.");
        return base.SendAsync(request, cancellationToken);
    }
}
