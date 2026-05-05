using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Jobs;
using OrchestratorApi.Services.Runner;

namespace OrchestratorApi.Endpoints.Jobs;

/// <summary>
/// Git surface scoped to one job: live status / diff against the
/// project's working tree, manual + LLM-assisted commit, the cached
/// commit detail (file list + diff) that backs the "Commit" view in
/// the protocol pane, and the IDE handoff. All routes operate on the
/// project's RootPath repository.
/// </summary>
public static class JobGitEndpoints
{
    public static void MapJobGitEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{jobId}/git/status", (string jobId, string? watchPath, GitService git) =>
            Results.Ok(git.GetStatus(jobId, watchPath)));

        group.MapGet("/{jobId}/git/diff", (string jobId, string? watchPath, string? path, GitService git) =>
            Results.Text(git.GetDiff(jobId, watchPath, path), "text/plain"));

        group.MapPost("/{jobId}/git/commit", (string jobId, string? watchPath, GitCommitRequest req, GitService git) =>
        {
            var result = git.Commit(jobId, watchPath, req.Message);
            return result.Success
                ? Results.Ok(new { sha = result.Sha })
                : Results.BadRequest(new { error = result.Error });
        });

        group.MapPost("/{jobId}/git/generate-message", async (string jobId, string? watchPath, GitService git, CancellationToken ct) =>
        {
            var result = await git.GenerateCommitMessageAsync(jobId, watchPath, ct);
            return result.Message is not null
                ? Results.Ok(new { message = result.Message })
                : Results.BadRequest(new { error = result.Error });
        });

        // Per-job commit details: returns the cached snapshot from job.json plus
        // a live re-derivation of the file list from `git show --name-status`,
        // so the detail view stays accurate even after history rewrites.
        group.MapGet("/{jobId}/commit", (string jobId, string? watchPath, JobScannerService scanner, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound();
            if (info.Commit == null) return Results.Ok(new { commit = (object?)null, files = Array.Empty<GitFileChange>() });

            var live = git.GetCommitFiles(jobId, watchPath, info.Commit.Sha);
            var files = live.Count > 0 ? live : info.Commit.Files.Select(p => new GitFileChange("?", p, 0, 0)).ToList();
            return Results.Ok(new { commit = info.Commit, files });
        });

        // Diff for the recorded commit, optionally scoped to one path. Lets
        // the detail view show the exact changes the task produced even long
        // after the working tree has moved on.
        group.MapGet("/{jobId}/commit/diff", (string jobId, string? watchPath, string? path, JobScannerService scanner, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info?.Commit == null) return Results.Text("", "text/plain");
            return Results.Text(git.GetCommitDiff(jobId, watchPath, info.Commit.Sha, path), "text/plain");
        });

        // Job-level commit aggregation: every commit attributed to this
        // job across all of its runs (deduped by SHA), plus the
        // auto-commit when present. Drives the protocol-pane "Commits
        // and change set" panel - the user must be able to see what
        // landed without having to drill into individual runs first.
        group.MapGet("/{jobId}/commits", (
            string jobId, string? watchPath,
            JobScannerService scanner, JobSessionLog sessions, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var events = sessions.ReadSessionEvents(jobId, watchPath);
            var lines = CliOutputLogParser.ParseFile(JobPaths.CliOutputLog(info.FolderPath));
            var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);

            var aggregate = JobCommitsAggregator.Aggregate(info, timeline.Runs,
                (before, after) => git.GetCommitsInShaRange(jobId, watchPath, before, after));

            return Results.Ok(aggregate);
        });

        // File list for one of the job's commits. Validates the SHA is
        // actually a known commit on this job before calling git so the
        // endpoint can't be coaxed into showing arbitrary repo history.
        group.MapGet("/{jobId}/commits/{sha}/files", (
            string jobId, string sha, string? watchPath,
            JobScannerService scanner, JobSessionLog sessions, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            if (!IsKnownJobCommit(info, sessions, jobId, watchPath, git, sha))
                return Results.NotFound(new { error = "Commit is not associated with this job." });
            var files = git.GetCommitFiles(jobId, watchPath, sha);
            return Results.Ok(new { sha, files });
        });

        group.MapGet("/{jobId}/commits/{sha}/diff", (
            string jobId, string sha, string? path, string? watchPath,
            JobScannerService scanner, JobSessionLog sessions, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            if (!IsKnownJobCommit(info, sessions, jobId, watchPath, git, sha))
                return Results.NotFound(new { error = "Commit is not associated with this job." });
            var diff = git.GetCommitDiff(jobId, watchPath, sha, path);
            return Results.Ok(new { diff });
        });

        group.MapPost("/{jobId}/open-in-vscode", (string jobId, string? watchPath, GitService git) =>
        {
            return git.OpenInVsCode(jobId, watchPath, out var error)
                ? Results.Ok()
                : Results.BadRequest(new { error });
        });
    }

    private static bool IsKnownJobCommit(
        JobInfo info, JobSessionLog sessions,
        string jobId, string? watchPath, GitService git, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return false;
        if (info.Commit != null && string.Equals(info.Commit.Sha, sha, StringComparison.OrdinalIgnoreCase))
            return true;
        var events = sessions.ReadSessionEvents(jobId, watchPath);
        var lines = CliOutputLogParser.ParseFile(JobPaths.CliOutputLog(info.FolderPath));
        var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
        var aggregate = JobCommitsAggregator.Aggregate(info, timeline.Runs,
            (before, after) => git.GetCommitsInShaRange(jobId, watchPath, before, after));
        return aggregate.Commits.Any(c => string.Equals(c.Sha, sha, StringComparison.OrdinalIgnoreCase));
    }
}
