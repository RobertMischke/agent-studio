namespace OrchestratorApi.Models;

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
        Notes = i.Notes
    };
}

public record ClientDetail
{
    public ClientSummary Identity { get; init; } = new();
    /// <summary>Number of jobs (across all states) currently attributed to this client.</summary>
    public int OwnedJobCount { get; init; }
    /// <summary>Up to ten most recent job ids attributed to this client (newest first).</summary>
    public List<string> RecentJobIds { get; init; } = [];
}
