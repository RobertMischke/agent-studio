using System.Text.Json;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// Writer for the per-run artefact folder. Layout (ADR-0031):
///
///   <RunsDirectory>/<run-id>/
///     pre-snapshot.json
///     pull-output.txt
///     npm-install-output.txt        (only if it ran)
///     dotnet-build-output.txt       (only if it ran)
///     start-stable-output.txt
///     verification.jsonl
///     resume-output.txt
///     post-snapshot.json
///     rollback-result.json          (only if rollback ran)
///     summary.md
///
/// Every method swallows IO errors and logs them; the orchestrator's
/// observable phase transitions are never blocked by a failed write.
/// </summary>
public sealed class RunFolder
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly JsonSerializerOptions JsonLineOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    private readonly string _root;
    private readonly ILogger _logger;

    public string Root => _root;
    public string RunId { get; }

    public RunFolder(string runsDirectory, string runId, ILogger logger)
    {
        RunId = runId;
        _root = Path.Combine(runsDirectory, runId);
        _logger = logger;
        try { Directory.CreateDirectory(_root); }
        catch (Exception ex) { _logger.LogWarning(ex, "create run folder {Path} failed", _root); }
    }

    public void WriteSnapshot(UpdateRunSnapshot snap)
    {
        var name = snap.Kind == "pre" ? "pre-snapshot.json" : "post-snapshot.json";
        WriteJson(name, snap);
    }

    public void AppendVerification(VerificationCheck row) => AppendVerificationLine("verification.jsonl", row);

    /// <summary>
    /// Re-running the 6-check matrix on the rollback path (ADR-0031) writes
    /// to a sibling file so the operator can tell the forward verification
    /// rows apart from the rollback verification rows without parsing.
    /// </summary>
    public void AppendRollbackVerification(VerificationCheck row) => AppendVerificationLine("rollback-verification.jsonl", row);

    private void AppendVerificationLine(string fileName, VerificationCheck row)
    {
        var path = Path.Combine(_root, fileName);
        try
        {
            var line = JsonSerializer.Serialize(row, JsonLineOpts);
            File.AppendAllText(path, line + Environment.NewLine);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "append {File} failed", fileName);
        }
    }

    public void WriteOutput(string fileName, string content)
    {
        var path = Path.Combine(_root, fileName);
        try { File.WriteAllText(path, content ?? ""); }
        catch (Exception ex) { _logger.LogWarning(ex, "write {Name} failed", fileName); }
    }

    public void WriteRollbackResult(RollbackResult result) => WriteJson("rollback-result.json", result);

    public void WriteSummary(string markdown)
    {
        try { File.WriteAllText(Path.Combine(_root, "summary.md"), markdown); }
        catch (Exception ex) { _logger.LogWarning(ex, "write summary.md failed"); }
    }

    private void WriteJson<T>(string fileName, T value)
    {
        var path = Path.Combine(_root, fileName);
        try
        {
            var json = JsonSerializer.Serialize(value, JsonOpts);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "write {Name} failed", fileName);
        }
    }
}
