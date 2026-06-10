
namespace AgentStudio.Runtime;

/// <summary>
/// Validates <see cref="ProductRuntimeEvent"/> instances against the value
/// sets in <c>docs/schemas/product-runtime-event.schema.json</c>. The on-disk
/// shape is JSON; we keep enum-like fields as strings so unfamiliar levels do
/// not crash the reader, and we check the constrained values here.
/// </summary>
/// <remarks>
/// Validation is tolerant: required fields are enforced, known enums are
/// checked, and lengths are bounded, but unknown payload keys flow through.
/// The bus and runtime streams must not be confused for each other; the
/// validator catches accidental cross-writes by rejecting events whose
/// payload looks like an <see cref="AgentMessage"/> envelope (carries an
/// <c>id</c> and a <c>kind</c>).
/// </remarks>
public static class RuntimeEventValidator
{
    public static readonly IReadOnlySet<string> Levels = new HashSet<string>(StringComparer.Ordinal)
    {
        "Trace", "Debug", "Info", "Warn", "Error", "Fatal",
    };

    public static readonly IReadOnlySet<string> Statuses = new HashSet<string>(StringComparer.Ordinal)
    {
        "Ok", "Failed", "Cancelled", "Timeout", "Skipped",
    };

    public static bool TryValidate(ProductRuntimeEvent? evt, out string? error)
    {
        if (evt is null) { error = "event is null"; return false; }
        if (evt.SchemaVersion != 1) { error = $"unsupported schemaVersion {evt.SchemaVersion}"; return false; }
        if (evt.Timestamp.Kind == DateTimeKind.Unspecified)
        {
            error = "timestamp must be UTC (kind != Unspecified)";
            return false;
        }
        if (string.IsNullOrWhiteSpace(evt.Level) || !Levels.Contains(evt.Level))
        { error = $"level invalid: '{evt.Level}'"; return false; }
        if (string.IsNullOrWhiteSpace(evt.Event) || evt.Event.Length > 80)
        { error = "event missing or too long (1..80)"; return false; }
        if (string.IsNullOrWhiteSpace(evt.Subsystem) || evt.Subsystem.Length > 80)
        { error = "subsystem missing or too long (1..80)"; return false; }
        if (evt.Operation is { Length: > 120 })
        { error = "operation too long (>120)"; return false; }
        if (!string.IsNullOrEmpty(evt.Status) && !Statuses.Contains(evt.Status!))
        { error = $"status invalid: '{evt.Status}'"; return false; }
        if (evt.Error is not null && string.IsNullOrWhiteSpace(evt.Error.Message))
        { error = "error.message required when error is present"; return false; }
        if (evt.Tags is { } tags)
        {
            if (tags.Count > 16) { error = "too many tags (>16)"; return false; }
            foreach (var t in tags)
            {
                if (string.IsNullOrWhiteSpace(t) || t.Length > 64) { error = "tag length out of range"; return false; }
            }
        }
        error = null;
        return true;
    }
}
