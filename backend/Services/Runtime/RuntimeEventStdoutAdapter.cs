using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runtime;

/// <summary>
/// Adapter that turns a stream of raw stdout/stderr lines from the built
/// software into validated <see cref="ProductRuntimeEvent"/> records, plus
/// parse warnings for lines that look like JSON events but fail to validate.
/// </summary>
/// <remarks>
/// <para>
/// Built software emits log output through whatever logging library it
/// already uses. The adapter sniffs each line for a leading <c>{</c> and
/// tries to parse it as a runtime event. Lines that do not start with
/// <c>{</c> are passed through unchanged: they remain part of the raw log
/// (<c>cli-output.log</c> or the producer's own file) but never feed the
/// runtime stream. This is the load-bearing rule from the task contract:
/// "do not require target projects to use one specific logging library";
/// the adapter is library-agnostic.
/// </para>
/// <para>
/// Lines that start with <c>{</c> but fail JSON parsing or schema validation
/// produce a <see cref="RuntimeEventParseWarning"/> so the producer can be
/// fixed; the raw line is preserved in the warning record.
/// </para>
/// </remarks>
public static class RuntimeEventStdoutAdapter
{
    public sealed record IngestResult(
        IReadOnlyList<ProductRuntimeEvent> Events,
        IReadOnlyList<RuntimeEventParseWarning> Warnings);

    public static IngestResult Ingest(IEnumerable<string> rawLines, string sourceLabel = "<stdout>")
    {
        ArgumentNullException.ThrowIfNull(rawLines);
        var events = new List<ProductRuntimeEvent>();
        var warnings = new List<RuntimeEventParseWarning>();
        var lineNumber = 0;
        foreach (var raw in rawLines)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // Cheap pre-filter: only lines that look like JSON objects can be
            // events. Everything else is plain log text and stays in the raw
            // log untouched. This keeps the adapter's hot path free of
            // exceptions for normal "INFO foo started" log lines.
            var trimmed = raw.TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '{') continue;

            ProductRuntimeEvent? evt = null;
            try
            {
                evt = JsonSerializer.Deserialize<ProductRuntimeEvent>(trimmed, RuntimeEventReader.JsonOptions);
            }
            catch (JsonException jex)
            {
                warnings.Add(new RuntimeEventParseWarning(sourceLabel, lineNumber, $"json parse: {jex.Message}", raw));
                continue;
            }

            if (evt is null) continue;

            // Some shapes deserialise without throwing (the JSON has no
            // required-field markers at the type level). Treat "missing
            // event/level/subsystem" as schema mismatch and route to
            // warnings, not events. This is what catches a producer that
            // emits {"msg":"hi"} on stdout - it parses, but it is not a
            // runtime event.
            if (string.IsNullOrEmpty(evt.Event) || string.IsNullOrEmpty(evt.Level) || string.IsNullOrEmpty(evt.Subsystem))
            {
                warnings.Add(new RuntimeEventParseWarning(sourceLabel, lineNumber, "missing required fields (event/level/subsystem)", raw));
                continue;
            }

            if (!RuntimeEventValidator.TryValidate(evt, out var validationError))
            {
                warnings.Add(new RuntimeEventParseWarning(sourceLabel, lineNumber, $"validation: {validationError}", raw));
                continue;
            }

            events.Add(evt);
        }

        return new IngestResult(events, warnings);
    }
}
