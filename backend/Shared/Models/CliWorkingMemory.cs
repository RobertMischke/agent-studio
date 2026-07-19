namespace AgentStudio.Shared;

/// <summary>
/// The closed set of <see cref="CliWorkingMemoryEntry.Kind"/> values for the
/// per-CLI Working-Memory panel (ASS-1748 / T1c). Memory and session states are
/// operator-deletable; auth and config are <b>protected</b> and never removable
/// through this surface - the whole point of T1c is that clearing a CLI's
/// accumulated working state can never log the operator out.
/// </summary>
public static class CliWorkingMemoryKinds
{
    /// <summary>A persistent memory file the CLI auto-loads (Claude/Gemini user memory). Deletable.</summary>
    public const string Memory = "memory";

    /// <summary>A session / transcript / prompt-history store the CLI reads or writes. Deletable.</summary>
    public const string Session = "session";

    /// <summary>The CLI's auth / credential material (Claude .credentials.json, Codex auth.json). Protected.</summary>
    public const string Auth = "auth";

    /// <summary>The CLI's base config file (settings.json / config.toml). Protected.</summary>
    public const string Config = "config";

    /// <summary>True only for the kinds the delete endpoint will ever act on.</summary>
    public static bool IsDeletable(string? kind) =>
        kind is Memory or Session;
}

/// <summary>
/// One persistent working-memory / session state a CLI keeps on disk, surfaced
/// per-CLI on the Admin/CLI page (ASS-1748 / T1c): its path, on-disk size, when
/// it was last touched, and a short content preview, plus whether the operator
/// may delete it. Auth / credential and base-config entries are reported with
/// <see cref="Deletable"/> = false so the panel can show them as protected and
/// the delete endpoint refuses them.
/// </summary>
public record CliWorkingMemoryEntry
{
    /// <summary>Stable id for the row (the absolute path; unique per CLI state).</summary>
    public string Id { get; init; } = "";

    /// <summary>One of <c>CliTypes</c>.</summary>
    public string CliType { get; init; } = "";

    /// <summary>One of <see cref="CliWorkingMemoryKinds"/>.</summary>
    public string Kind { get; init; } = "";

    /// <summary>Short human label, e.g. <c>"User memory"</c> or <c>"Session store"</c>.</summary>
    public string Label { get; init; } = "";

    /// <summary>Absolute path of the file or directory.</summary>
    public string Path { get; init; } = "";

    /// <summary>True when <see cref="Path"/> is a directory (its size is an aggregate).</summary>
    public bool IsDirectory { get; init; }

    /// <summary>On-disk size in bytes (aggregate for a directory, capped by the walk budget).</summary>
    public long SizeBytes { get; init; }

    /// <summary>Number of items in a directory (null for a single file).</summary>
    public int? ItemCount { get; init; }

    /// <summary>UTC last-write time (newest child for a directory).</summary>
    public DateTime? LastModifiedUtc { get; init; }

    /// <summary>First chunk of the file's text, or a child summary for a directory. Null when not previewable.</summary>
    public string? Preview { get; init; }

    /// <summary>Whether the operator may delete this state. Always false for auth / config.</summary>
    public bool Deletable { get; init; }

    /// <summary>Optional extra detail, e.g. why a protected entry can never be deleted.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// The per-CLI Working-Memory report (ASS-1748 / T1c): every persistent
/// memory / session state plus the protected auth / config entries for one CLI.
/// </summary>
public record CliWorkingMemoryReport
{
    /// <summary>One of <c>CliTypes</c>.</summary>
    public string CliType { get; init; } = "";

    /// <summary>True when the CLI's config root exists on disk.</summary>
    public bool Available { get; init; }

    /// <summary>The CLI's resolved config root (e.g. <c>~/.claude</c>), when known.</summary>
    public string? Root { get; init; }

    /// <summary>UTC time the report was built.</summary>
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;

    /// <summary>The states, deletable first, then protected.</summary>
    public List<CliWorkingMemoryEntry> Entries { get; init; } = [];
}

/// <summary>Outcome of a <see cref="CliWorkingMemoryReport"/> delete request.</summary>
public enum CliWorkingMemoryDeleteStatus
{
    /// <summary>The entry was deleted.</summary>
    Deleted,
    /// <summary>No deletable entry matched the requested path.</summary>
    NotFound,
    /// <summary>The path resolved to a protected (auth / config) entry; refused.</summary>
    Protected,
    /// <summary>An I/O error prevented the delete.</summary>
    Error,
}

/// <summary>Result of deleting one working-memory state, with the refreshed report.</summary>
public record CliWorkingMemoryDeleteResult
{
    public CliWorkingMemoryDeleteStatus Status { get; init; }
    public string? Message { get; init; }
    public long FreedBytes { get; init; }
    public CliWorkingMemoryReport? Report { get; init; }
}
