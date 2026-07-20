using System.Text.RegularExpressions;

namespace AgentStudio.Diagnostics;

/// <summary>
/// Runner -> Server artifact-ingestion API under <c>/api/runner/artifacts</c>.
/// Remote runners send screenshots and result files to the server, which owns
/// the durable task folder and workspace evidence commit.
/// </summary>
public static class ArtifactIngestionEndpoints
{
    private static readonly Regex UnsafeSegment = new(@"(^|[\\/])\.\.([\\/]|$)", RegexOptions.Compiled);

    public static void MapArtifactIngestionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/runner/artifacts", (
            ArtifactIngestRequest req,
            ITaskScanner scanner,
            AttemptAuthorityService authority,
            WorkspaceArtifactCommitService artifactCommits,
            ILoggerFactory loggerFactory) =>
        {
            var logger = loggerFactory.CreateLogger("AgentStudio.Diagnostics.ArtifactIngestionEndpoints");
            if (req.Artifacts is null || req.Artifacts.Count == 0)
                return Results.Ok(new ArtifactIngestResponse(req.TaskKey, 0, [], "no artifacts"));

            var task = ResolveTask(scanner, req.TaskKey);
            if (task is null)
                return Results.NotFound(new ArtifactIngestResponse(req.TaskKey, 0, [], $"No task '{req.TaskKey}'."));

            var projection = authority.GetTaskProjection(req.TaskKey);
            if (string.IsNullOrWhiteSpace(req.AttemptId) || !req.Fence.HasValue
                || !req.AuthorityEpoch.HasValue || string.IsNullOrWhiteSpace(req.IdempotencyKey))
            {
                if (!projection.LegacyTask)
                    return Results.Conflict(new AttemptWriteResult(
                        AttemptWriteStatus.Invalid, req.AttemptId ?? string.Empty,
                        "Canonical runner writes require AttemptId, Fence, AuthorityEpoch, and IdempotencyKey."));
            }
            else
            {
                var digestInput = string.Join("\n", req.Artifacts
                    .OrderBy(x => x.Path, StringComparer.Ordinal)
                    .Select(x => $"{x.Path}:{AttemptAuthorityService.Hash(x.ContentBase64 ?? string.Empty)}"));
                var accepted = authority.RecordEvidenceDigest(
                    new AttemptWriteReference(req.AttemptId, req.Fence.Value, req.AuthorityEpoch.Value, req.IdempotencyKey),
                    "artifact-set:" + AttemptAuthorityService.Hash(digestInput));
                if (accepted.Status == AttemptWriteStatus.Duplicate)
                    return Results.Ok(new ArtifactIngestResponse(req.TaskKey, 0, [], "duplicate delivery"));
                if (accepted.Status != AttemptWriteStatus.Accepted)
                    return Results.Conflict(accepted);
            }

            ArtifactIngestResponse written;
            try
            {
                written = WriteArtifacts(task, req);
            }
            catch (ArtifactIngestException ex)
            {
                return Results.BadRequest(new ArtifactIngestResponse(req.TaskKey, 0, [], ex.Message));
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to ingest artifacts for '{req.TaskKey}': {ex.Message}");
            }

            var commit = artifactCommits.TryCommitArtifactUpload(
                null,
                task.Id,
                task.FolderPath,
                written.Files);

            var status = commit.Success
                ? commit.DidCommit ? "committed" : $"skipped:{commit.Error}"
                : $"failed:{commit.Error}";
            logger.LogInformation(
                "runner-artifact-ingest taskKey={TaskKey} jobId={JobId} uploaded={Uploaded} commitStatus={CommitStatus} sha={Sha}",
                req.TaskKey, task.Id, written.Uploaded, status, commit.Sha ?? "");

            return Results.Ok(written with
            {
                CommitSha = commit.Sha,
                CommitStatus = status
            });
        });
    }

    internal static ArtifactIngestResponse WriteArtifacts(TaskInfo task, ArtifactIngestRequest req)
    {
        if (string.IsNullOrWhiteSpace(task.FolderPath))
            throw new ArtifactIngestException("Task folder is missing.");

        var resultsDir = Path.Combine(task.FolderPath, TaskPaths.ResultsDirName);
        Directory.CreateDirectory(resultsDir);

        var files = new List<string>();
        foreach (var artifact in req.Artifacts)
        {
            var rel = NormalizeResultsPath(artifact.Path);
            var destination = Path.GetFullPath(Path.Combine(task.FolderPath, rel));
            var resultsRoot = Path.GetFullPath(resultsDir) + Path.DirectorySeparatorChar;
            if (!destination.StartsWith(resultsRoot, StringComparison.OrdinalIgnoreCase))
                throw new ArtifactIngestException($"Artifact path escapes results/: {artifact.Path}");

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(artifact.ContentBase64 ?? string.Empty);
            }
            catch (FormatException)
            {
                throw new ArtifactIngestException($"Artifact content is not valid base64: {artifact.Path}");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
            files.Add(rel.Replace('\\', '/'));
        }

        return new ArtifactIngestResponse(req.TaskKey, files.Count, files);
    }

    internal static string NormalizeResultsPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArtifactIngestException("Artifact path is required.");

        var normalized = path.Trim().Replace('\\', '/');
        if (Path.IsPathRooted(normalized) || UnsafeSegment.IsMatch(normalized))
            throw new ArtifactIngestException($"Artifact path must stay under results/: {path}");

        normalized = normalized.StartsWith("results/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : "results/" + normalized.TrimStart('/');

        if (string.Equals(normalized, "results/", StringComparison.OrdinalIgnoreCase)
            || normalized.EndsWith("/", StringComparison.Ordinal))
        {
            throw new ArtifactIngestException($"Artifact path must name a file: {path}");
        }

        return normalized;
    }

    private static TaskInfo? ResolveTask(ITaskScanner scanner, string taskKey)
    {
        if (string.IsNullOrWhiteSpace(taskKey)) return null;
        return scanner.ScanAllJobs().FirstOrDefault(t =>
            string.Equals(t.TaskKey, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Id, taskKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(t.Key, taskKey, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class ArtifactIngestException : Exception
{
    public ArtifactIngestException(string message) : base(message) { }
}
