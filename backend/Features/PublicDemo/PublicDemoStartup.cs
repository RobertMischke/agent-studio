namespace AgentStudio.PublicDemo;

/// <summary>
/// Startup composition for the public read-only edge. The contract is built
/// once from configuration and the committed allowlist and registered as a
/// singleton, so there is no request path, management command, or project
/// setting that can widen the visitor surface after boot.
/// </summary>
public static class PublicDemoStartup
{
    public const string SectionName = "PublicDemo";

    internal static readonly string[] DefaultProjects = ["demo-app", "demo-platform"];

    /// <summary>Read bodies are tiny. The ceiling exists to make a large upload attempt cheap to refuse.</summary>
    internal const long DefaultMaxRequestBodyBytes = 16 * 1024;

    internal const int DefaultRequestsPerWindow = 240;
    internal const int DefaultWindowSeconds = 60;
    internal const int DefaultViewerSessionMinutes = 120;

    /// <summary>
    /// Registers the edge contract and its bounded collaborators. The contract
    /// is resolved from the built host's configuration rather than captured
    /// during registration, so a deployment override always reaches it.
    /// <see cref="EnsureValidAtStartup"/> materializes and validates it while
    /// the host is still starting.
    /// </summary>
    public static IServiceCollection AddPublicDemoEdge(this IServiceCollection services)
    {
        services.AddSingleton(sp => BuildContract(sp.GetRequiredService<IConfiguration>()));
        services.AddSingleton(sp => new PublicEdgeRateLimiter(
            sp.GetRequiredService<PublicEdgeContract>(),
            sp.GetService<TimeProvider>() ?? TimeProvider.System));
        services.AddSingleton(sp => new PublicDemoProjectScope(
            sp.GetRequiredService<PublicEdgeContract>(),
            sp.GetRequiredService<AgentStudio.Registry.ProjectRegistry>()));
        return services;
    }

    /// <summary>
    /// Materializes the contract during boot and refuses to continue when the
    /// public demo profile is armed with a broken visitor boundary.
    /// </summary>
    public static void EnsureValidAtStartup(IServiceProvider services, IConfiguration configuration)
    {
        var contract = services.GetRequiredService<PublicEdgeContract>();
        if (PublicDemoProfile.IsActive(configuration)) Validate(contract);
    }

    public static PublicEdgeContract BuildContract(IConfiguration configuration)
    {
        var section = configuration.GetSection(SectionName);
        var projects = section.GetSection("Projects").Get<string[]>();
        if (projects is null || projects.Length == 0) projects = DefaultProjects;

        return new PublicEdgeContract
        {
            Routes = PublicEdgeAllowlist.Routes,
            Projects = projects,
            MaxRequestBodyBytes = section.GetValue("MaxRequestBodyBytes", DefaultMaxRequestBodyBytes),
            RequestsPerWindow = section.GetValue("RequestsPerWindow", DefaultRequestsPerWindow),
            Window = TimeSpan.FromSeconds(section.GetValue("WindowSeconds", DefaultWindowSeconds)),
            ViewerSessionLifetime = TimeSpan.FromMinutes(section.GetValue("ViewerSessionMinutes", DefaultViewerSessionMinutes)),
            AllowlistDigest = PublicEdgeAllowlist.Digest(PublicEdgeAllowlist.Routes),
        };
    }

    /// <summary>
    /// Fail the boot rather than serve a demo whose visitor boundary is not
    /// intact. The dossier's launch invariant applies here too: if the server
    /// cannot prove the lock at startup, the public demo does not start.
    /// </summary>
    public static void Validate(PublicEdgeContract contract)
    {
        var problems = new List<string>();
        if (contract.Routes.Count == 0) problems.Add("the read allowlist is empty");
        if (contract.Projects.Count == 0) problems.Add("no demo project is configured");
        if (contract.MaxRequestBodyBytes <= 0) problems.Add("MaxRequestBodyBytes must be positive");
        if (contract.RequestsPerWindow <= 0) problems.Add("RequestsPerWindow must be positive");
        if (contract.Window <= TimeSpan.Zero) problems.Add("WindowSeconds must be positive");
        if (contract.ViewerSessionLifetime <= TimeSpan.Zero) problems.Add("ViewerSessionMinutes must be positive");

        var unsafeRoutes = contract.Routes
            .Where(route => !PublicEdgePolicy.IsSafeMethod(route.Method))
            .Select(route => $"{route.Method} {route.Template}")
            .ToList();
        if (unsafeRoutes.Count > 0)
            problems.Add("the allowlist contains unsafe methods: " + string.Join(", ", unsafeRoutes));

        if (problems.Count == 0) return;
        throw new InvalidOperationException(
            $"The '{PublicDemoProfile.ProfileName}' profile cannot start: {string.Join("; ", problems)}.");
    }
}

/// <summary>
/// The one public endpoint that describes the edge. It is deliberately readable
/// by anyone: publishing the ceilings and the allowlist digest lets an external
/// probe verify the deployed boundary without an operator credential.
/// </summary>
public static class PublicDemoEndpoints
{
    public static void MapPublicDemoEndpoints(this WebApplication app)
    {
        app.MapGet("/api/public-demo/edge", (IConfiguration configuration, PublicEdgeContract contract) =>
            Results.Ok(PublicDemoEdgeStatus.From(configuration, contract)));
    }
}

/// <summary>
/// Wire shape of the edge contract. Shared by <c>/api/public-demo/edge</c> and
/// the frontend bootstrap payload so the UI's read-only explanation and the
/// enforced boundary cannot drift apart.
/// </summary>
public sealed record PublicDemoEdgeStatus
{
    public required bool Active { get; init; }
    public required bool ReadOnly { get; init; }
    public required string Profile { get; init; }
    public required IReadOnlyList<string> Projects { get; init; }
    public required string AllowlistDigest { get; init; }
    public required int AllowlistRouteCount { get; init; }
    public required long MaxRequestBodyBytes { get; init; }
    public required int RequestsPerWindow { get; init; }
    public required int WindowSeconds { get; init; }

    /// <summary>The deployment profile name used by the release manifest.</summary>
    public const string DeploymentProfile = "public-demo-readonly";

    public static PublicDemoEdgeStatus From(IConfiguration configuration, PublicEdgeContract contract)
    {
        var active = PublicDemoProfile.IsActive(configuration);
        return new PublicDemoEdgeStatus
        {
            Active = active,
            ReadOnly = active,
            Profile = active ? DeploymentProfile : AgentStudio.Security.SecurityProfiles.ActiveProfile(configuration),
            Projects = active ? contract.Projects : [],
            AllowlistDigest = contract.AllowlistDigest,
            AllowlistRouteCount = contract.Routes.Count,
            MaxRequestBodyBytes = contract.MaxRequestBodyBytes,
            RequestsPerWindow = contract.RequestsPerWindow,
            WindowSeconds = (int)contract.Window.TotalSeconds,
        };
    }
}
