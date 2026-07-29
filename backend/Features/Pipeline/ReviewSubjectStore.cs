using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// Canonical, fenced handoff from a completed coding run to completion review.
/// Remote review must read this record instead of reconstructing a subject from
/// local session events that do not exist on the Task Server.
/// </summary>
public sealed record ReviewSubjectRecord
{
    public int Version { get; init; } = 1;
    public string TaskKey { get; init; } = "";
    public string Project { get; init; } = "";
    public string Repository { get; init; } = "";
    public string ResultSha { get; init; } = "";
    public string AttemptChainId { get; init; } = "";
    public string Executor { get; init; } = "";
    public string LeaseId { get; init; } = "";
    public long FencingToken { get; init; }
    /// <summary>
    /// Immutable delivery ref from the source RunAttempt's ResultEnvelope.
    /// This is distinct from the legacy <see cref="ResultRef"/>, which may have
    /// named a mutable salvage branch.
    /// </summary>
    public string? ImmutableResultRef { get; init; }
    public string? ResultRef { get; init; }
    public string? IntegrationBranch { get; init; }
    public DateTimeOffset CompletedAtUtc { get; init; }
}

public static class ReviewSubjectStore
{
    public const string FileName = "review-subject.json";

    private static readonly Regex FullSha = new(
        "^[0-9a-fA-F]{40,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static string PathFor(string taskFolder)
        => Path.Combine(TaskPaths.LogsDir(taskFolder), FileName);

    public static bool IsValidResultSha(string? value)
        => !string.IsNullOrWhiteSpace(value) && FullSha.IsMatch(value);

    public static void Write(string taskFolder, ReviewSubjectRecord subject)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskFolder);
        ArgumentNullException.ThrowIfNull(subject);
        if (!IsValidResultSha(subject.ResultSha))
            throw new ArgumentException("ResultSha must be a full Git commit SHA.", nameof(subject));
        if (string.IsNullOrWhiteSpace(subject.AttemptChainId))
            throw new ArgumentException("AttemptChainId is required.", nameof(subject));

        var path = PathFor(taskFolder);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(subject, Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
            catch (Exception ex)
            {
                SilentCatch.Note(ex, "ReviewSubjectStore: temporary file cleanup");
                // Best effort cleanup of a file that was never authoritative.
            }
        }
    }

    public static ReviewSubjectRecord? Read(string taskFolder)
    {
        var path = PathFor(taskFolder);
        if (!File.Exists(path)) return null;
        try
        {
            var subject = JsonSerializer.Deserialize<ReviewSubjectRecord>(File.ReadAllText(path), Json);
            return subject is not null
                   && IsValidResultSha(subject.ResultSha)
                   && !string.IsNullOrWhiteSpace(subject.AttemptChainId)
                ? subject
                : null;
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "ReviewSubjectStore: malformed or unreadable subject");
            return null;
        }
    }
}
