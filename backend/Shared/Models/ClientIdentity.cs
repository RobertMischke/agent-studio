namespace AgentStudio.Shared;

/// <summary>
/// Identity record for one client of the Task Access Layer. A "client" is
/// anything that talks to the API: a human-driven UI session, a CLI agent
/// instance, an external tool, or a backend service.
///
/// Stored as JSON files under <c>&lt;TaskRepository&gt;/identities/&lt;id&gt;.json</c>.
/// Loaded on backend boot. Mutations through <c>/api/clients/*</c> are the
/// single writer; no other service writes identity files directly.
///
/// Pairs with <c>docs/schemas/client-identity.schema.json</c>.
///
/// This is a registration boundary, not a security model: the door has a
/// sign, every visitor signs in. Cryptographic signing is a follow-up.
/// </summary>
public record ClientIdentity
{
    /// <summary>Stable, immutable, kebab-case slug. Assigned by the API on first registration.</summary>
    public string Id { get; init; } = "";

    /// <summary>Free-form human-readable name. Re-registering the same value is idempotent.</summary>
    public string DisplayName { get; init; } = "";

    /// <summary>Optional single grapheme used as a visual marker on cards and chips.</summary>
    public string? Emoji { get; init; }

    /// <summary>Optional CSS hex colour applied to the chip background.</summary>
    public string? Colour { get; init; }

    /// <summary>What kind of caller this identity represents. Soft-delete flips this to "retired".</summary>
    public ClientIdentityKind Kind { get; init; } = ClientIdentityKind.Human;

    /// <summary>UTC timestamp the identity was first registered. Immutable.</summary>
    public DateTime RegisteredAt { get; init; }

    /// <summary>UTC timestamp of the most recent request bearing this clientId. Updated on every authenticated read or write.</summary>
    public DateTime? LastSeenAt { get; init; }

    /// <summary>Optional monthly token cap. Null disables the cap.</summary>
    public long? TokenBudgetMonthly { get; init; }

    /// <summary>Free-form operator notes.</summary>
    public string? Notes { get; init; }

    /// <summary>
    /// User's preferred CLI for new tasks ("claude", "codex", "gemini").
    /// The orchestrator reads this on every chat turn so a
    /// "create me three tasks" request lands on the user's actual default
    /// instead of the hardcoded "claude" fallback. Null until first set.
    /// </summary>
    public string? DefaultCliType { get; init; }

    /// <summary>
    /// User's preferred model id for new tasks (e.g. "claude-haiku-4-5").
    /// Surfaced into the per-turn orchestrator prompt. Null until first set.
    /// </summary>
    public string? DefaultModel { get; init; }

    /// <summary>
    /// User's preferred thinking / reasoning level for new tasks. Null means
    /// use the selected model's default level.
    /// </summary>
    public string? DefaultThinkingLevel { get; init; }

    /// <summary>Latest daemon startup proof: <c>ready</c> or <c>read-only</c>; null for legacy clients.</summary>
    public string? RunnerGitStatus { get; init; }
    public string? RunnerGitDetail { get; init; }
    public DateTime? RunnerGitCheckedAt { get; init; }

    /// <summary>Persisted operator lifecycle. A drain blocks claims; retire completes after active slots reach zero.</summary>
    public DateTime? DrainRequestedAt { get; init; }
    public DateTime? RetireRequestedAt { get; init; }

    /// <summary>Latest daemon activity projection, reported on every claim poll.</summary>
    public string? RunnerDaemonState { get; init; }
    public DateTime? RunnerLastClaimAt { get; init; }
    public int? RunnerActiveSlots { get; init; }
    public int? RunnerAvailableSlots { get; init; }
}

public enum ClientIdentityKind
{
    Human,
    AgentInstance,
    ExternalTool,
    Service,
    Retired
}

public static class ClientIdentityKinds
{
    public const string Human = "human";
    public const string AgentInstance = "agent-instance";
    public const string ExternalTool = "external-tool";
    public const string Service = "service";
    public const string Retired = "retired";

    public static ClientIdentityKind Parse(string? value) => value?.ToLowerInvariant() switch
    {
        AgentInstance => ClientIdentityKind.AgentInstance,
        ExternalTool => ClientIdentityKind.ExternalTool,
        Service => ClientIdentityKind.Service,
        Retired => ClientIdentityKind.Retired,
        _ => ClientIdentityKind.Human
    };
}

/// <summary>
/// Bootstrap convention: a default identity exists on every backend so
/// historical jobs (no <c>ownerClientId</c> on disk) get a non-null
/// attribution on first migration. Configured via
/// <c>Environment:DefaultIdentityName</c> and <c>Environment:DefaultIdentityEmoji</c>
/// in <c>appsettings.Local.json</c>.
/// </summary>
public static class DefaultClientIdentity
{
    public const string Id = "local-default";
}

public record RegisterClientRequest
{
    public string DisplayName { get; init; } = "";
    public string? Emoji { get; init; }
    public string? Colour { get; init; }
    public string? Kind { get; init; }
    public long? TokenBudgetMonthly { get; init; }
    public string? Notes { get; init; }
}

public record ClientSummary
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string? Emoji { get; init; }
    public string? Colour { get; init; }
    public string Kind { get; init; } = ClientIdentityKinds.Human;
    public DateTime RegisteredAt { get; init; }
    public DateTime? LastSeenAt { get; init; }
    public long? TokenBudgetMonthly { get; init; }
    public string? Notes { get; init; }
    public string? DefaultCliType { get; init; }
    public string? DefaultModel { get; init; }
    public string? DefaultThinkingLevel { get; init; }
    public string? RunnerGitStatus { get; init; }
    public string? RunnerGitDetail { get; init; }
    public DateTime? RunnerGitCheckedAt { get; init; }
    public DateTime? DrainRequestedAt { get; init; }
    public DateTime? RetireRequestedAt { get; init; }
    public string? RunnerDaemonState { get; init; }
    public DateTime? RunnerLastClaimAt { get; init; }
    public int? RunnerActiveSlots { get; init; }
    public int? RunnerAvailableSlots { get; init; }

    public static ClientSummary From(ClientIdentity i) => new()
    {
        Id = i.Id,
        DisplayName = i.DisplayName,
        Emoji = i.Emoji,
        Colour = i.Colour,
        Kind = i.Kind switch
        {
            ClientIdentityKind.AgentInstance => ClientIdentityKinds.AgentInstance,
            ClientIdentityKind.ExternalTool => ClientIdentityKinds.ExternalTool,
            ClientIdentityKind.Service => ClientIdentityKinds.Service,
            ClientIdentityKind.Retired => ClientIdentityKinds.Retired,
            _ => ClientIdentityKinds.Human
        },
        RegisteredAt = i.RegisteredAt,
        LastSeenAt = i.LastSeenAt,
        TokenBudgetMonthly = i.TokenBudgetMonthly,
        Notes = i.Notes,
        DefaultCliType = i.DefaultCliType,
        DefaultModel = i.DefaultModel,
        DefaultThinkingLevel = i.DefaultThinkingLevel,
        RunnerGitStatus = i.RunnerGitStatus,
        RunnerGitDetail = i.RunnerGitDetail,
        RunnerGitCheckedAt = i.RunnerGitCheckedAt,
        DrainRequestedAt = i.DrainRequestedAt,
        RetireRequestedAt = i.RetireRequestedAt,
        RunnerDaemonState = i.RunnerDaemonState,
        RunnerLastClaimAt = i.RunnerLastClaimAt,
        RunnerActiveSlots = i.RunnerActiveSlots,
        RunnerAvailableSlots = i.RunnerAvailableSlots
    };
}

public record RunnerGitCapabilityRequest
{
    public string Status { get; init; } = "";
    public string? Detail { get; init; }
    public DateTime CheckedAt { get; init; }
}
/// Body for <c>PUT /api/clients/{id}/defaults</c>. Each field is independent;
/// omit a field to leave it untouched. Set a field to an empty string to
/// clear that side.
/// </summary>
public record SetClientDefaultsRequest
{
    public string? DefaultCliType { get; init; }
    public string? DefaultModel { get; init; }
    public string? DefaultThinkingLevel { get; init; }
}

/// <summary>
/// Read-side shape of <c>GET /api/clients/{id}/defaults</c>. Always returns
/// the (possibly null) current values for the identity.
/// </summary>
public record ClientDefaultsResponse
{
    public string Id { get; init; } = "";
    public string? DefaultCliType { get; init; }
    public string? DefaultModel { get; init; }
    public string? DefaultThinkingLevel { get; init; }
}

public record ClientDetail
{
    public ClientSummary Identity { get; init; } = new();
    /// <summary>Number of jobs (across all states) currently attributed to this client.</summary>
    public int OwnedJobCount { get; init; }
    /// <summary>Up to ten most recent job ids attributed to this client (newest first).</summary>
    public List<string> RecentJobIds { get; init; } = [];
}
