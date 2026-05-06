using System.Text;
using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runtime;

/// <summary>
/// Reads <see cref="ProductRuntimeEvent"/> records from a JSONL file (one
/// event per line). Adapter-style: the reader does not care which producer
/// wrote the file (backend stdout sniffer, file-tail adapter, Playwright
/// capture, custom emitter); it only enforces the on-disk shape.
/// </summary>
/// <remarks>
/// Malformed lines never abort the read. Each rejection becomes a
/// <see cref="RuntimeEventParseWarning"/> in <see cref="RuntimeEventReadResult"/>
/// alongside the raw line, so reviewers can still inspect what the
/// producer emitted. This mirrors how <c>AgentMessageBusStore.LoadFile</c>
/// handles malformed bus lines, and is the load-bearing rule from the
/// task contract: "preserve raw logs and expose parse warnings when events
/// are malformed".
/// </remarks>
public sealed class RuntimeEventReader
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public RuntimeEventReadResult Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var events = new List<ProductRuntimeEvent>();
        var warnings = new List<RuntimeEventParseWarning>();

        if (!File.Exists(path)) return new RuntimeEventReadResult(events, warnings);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var lineNumber = 0;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            if (string.IsNullOrWhiteSpace(line)) continue;

            ProductRuntimeEvent? evt = null;
            try
            {
                evt = JsonSerializer.Deserialize<ProductRuntimeEvent>(line, JsonOptions);
            }
            catch (JsonException jex)
            {
                warnings.Add(new RuntimeEventParseWarning(path, lineNumber, $"json parse: {jex.Message}", line));
                continue;
            }

            if (evt is null)
            {
                warnings.Add(new RuntimeEventParseWarning(path, lineNumber, "deserialised to null", line));
                continue;
            }

            if (!RuntimeEventValidator.TryValidate(evt, out var validationError))
            {
                warnings.Add(new RuntimeEventParseWarning(path, lineNumber, $"validation: {validationError}", line));
                continue;
            }

            events.Add(evt);
        }

        return new RuntimeEventReadResult(events, warnings);
    }
}

/// <summary>
/// Result of reading a runtime JSONL file. Events are valid and parsed in
/// file order; warnings keep the raw line so the producer can be debugged
/// without re-running the failing scenario.
/// </summary>
public sealed record RuntimeEventReadResult(
    IReadOnlyList<ProductRuntimeEvent> Events,
    IReadOnlyList<RuntimeEventParseWarning> Warnings);
