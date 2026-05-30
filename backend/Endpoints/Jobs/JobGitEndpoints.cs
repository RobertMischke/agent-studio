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
public static class TaskGitEndpoints
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
        group.MapGet("/{jobId}/commit", (string jobId, string? watchPath, TaskScannerService scanner, GitService git) =>
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
        group.MapGet("/{jobId}/commit/diff", (string jobId, string? watchPath, string? path, TaskScannerService scanner, GitService git) =>
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
            TaskScannerService scanner, TaskSessionLog sessions, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var events = sessions.ReadSessionEvents(jobId, watchPath);
            var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(info.FolderPath));
            var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);

            var aggregate = TaskCommitsAggregator.Aggregate(info, timeline.Runs,
                (before, after) => git.GetCommitsInShaRange(jobId, watchPath, before, after));

            return Results.Ok(aggregate);
        });

        // Recent commits on the branch - backs the git pane's "+ Add commit"
        // operator override (ADR "Commit-Attribution-Regel"). Read-only.
        group.MapGet("/{jobId}/git/recent-commits", (
            string jobId, string? watchPath, int? limit,
            TaskScannerService scanner, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            var commits = git.GetRecentCommits(jobId, watchPath, limit ?? 20);
            return Results.Ok(new { commits });
        });

        // File list for one of the job's commits. Validates the SHA is
        // actually a known commit on this job before calling git so the
        // endpoint can't be coaxed into showing arbitrary repo history.
        group.MapGet("/{jobId}/commits/{sha}/files", (
            string jobId, string sha, string? watchPath,
            TaskScannerService scanner, TaskSessionLog sessions, GitService git) =>
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
            TaskScannerService scanner, TaskSessionLog sessions, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            if (!IsKnownJobCommit(info, sessions, jobId, watchPath, git, sha))
                return Results.NotFound(new { error = "Commit is not associated with this job." });
            var diff = git.GetCommitDiff(jobId, watchPath, sha, path);
            return Results.Ok(new { diff });
        });

        // Per-job hygiene: project-level snapshot overlaid with whether the
        // job carries a platform-owned commit stamp and whether accepted task
        // work appears uncommitted. The job-detail review/completed strip
        // polls this; the project header polls /api/git/hygiene instead.
        //
        // Worktree-isolation rule: the working tree is shared across the
        // whole repository, so `acceptedTaskUncommitted` must only fire on
        // the task that owns whatever the agent is currently editing -
        // i.e. the runner's active job for the project. We resolve that
        // via TaskRunnerService and pass `isActiveJob` to GitService so
        // non-active tasks get the warning suppressed at the data layer,
        // not just hidden in the UI.
        group.MapGet("/{jobId}/git/hygiene", (
            string jobId, string? watchPath,
            GitService git, TaskScannerService scanner, TaskRunnerService runner) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            var status = runner.GetStatus();
            var isActive = info != null
                && status.Projects.TryGetValue(info.ProjectName, out var project)
                && string.Equals(project.ActiveJobId, info.Id, StringComparison.Ordinal);
            var hygiene = git.GetJobHygiene(jobId, watchPath, isActive);
            return string.IsNullOrEmpty(hygiene.Error)
                ? Results.Ok(hygiene)
                : Results.NotFound(new { error = hygiene.Error });
        });

        // Manual "commit accepted task evidence" action. Re-uses the same
        // platform-owned commit-message path the auto-commit on
        // 3-progress -> 4-auto-review uses (Haiku via runtime/commit-message.md
        // with a deterministic fallback) so the user gets one consistent
        // commit voice regardless of which CLI did the work. Stamps the
        // produced SHA onto TaskInfo.Commit so the detail view picks it up,
        // and writes a [commit] orchestrator-chat entry into the activity
        // log so the action is visible in the protocol pane.
        group.MapPost("/{jobId}/git/commit-accepted-evidence",
            async (string jobId, string? watchPath,
                   GitService git, TaskScannerService scanner, TaskMutationService mutations,
                   OrchestratorChatLog chat, CancellationToken ct) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            var (result, message) = await git.AutoCommitAsync(jobId, watchPath, ct);
            if (!result.Success || string.IsNullOrWhiteSpace(result.Sha))
            {
                return Results.BadRequest(new { error = result.Error ?? "Commit failed", message });
            }

            var files = git.GetCommitFiles(jobId, watchPath, result.Sha);
            var commitInfo = new TaskCommitInfo
            {
                Sha = result.Sha,
                ShortSha = result.Sha.Length > 7 ? result.Sha[..7] : result.Sha,
                Message = message ?? "",
                FilesChanged = files.Count,
                Files = files.Select(f => f.Path).ToList(),
                At = DateTime.UtcNow
            };
            mutations.SetJobCommitOnFolder(info.FolderPath, commitInfo);
            git.InvalidateHygieneCache();

            // Refresh and chat-log entry: the protocol-pane activity-log
            // reader will pick this up as a [commit] line and render it
            // alongside agent + orchestrator messages.
            var refreshed = scanner.FindJob(jobId, watchPath) ?? info;
            try
            {
                chat.Append(refreshed, OrchestratorMessageKind.Decision,
                    $"Committed accepted task evidence: {commitInfo.ShortSha} \"{(message ?? "").Split('\n')[0]}\" ({commitInfo.FilesChanged} file{(commitInfo.FilesChanged == 1 ? "" : "s")})");
            }
            catch { /* chat-log is best-effort */ }

            return Results.Ok(new { commit = commitInfo });
        });

        // Operator override: exclude a commit the rule engine attributed to
        // this task (e.g. the operator recognizes it belongs to a sibling
        // task). Moves it into excludedCommits with a manual marker. ADR
        // "Commit-Attribution-Regel". Mutation goes through TaskMutationService
        // so the API-only job-folder rule holds.
        group.MapPost("/{jobId}/commits/{sha}/exclude", (
            string jobId, string sha, string? watchPath,
            TaskScannerService scanner, TaskMutationService mutations) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });
            return mutations.ExcludeCommit(jobId, sha, watchPath)
                ? Results.Ok(new { sha, excluded = true })
                : Results.BadRequest(new { error = "Could not exclude commit." });
        });

        // Operator override: include a commit in this task's set - either
        // restoring one the rule engine excluded (manual-include-after-exclude)
        // or adding one it never saw via "+ Add commit" (manual-add). The
        // optional body carries commit metadata for the add-from-recent case.
        group.MapPost("/{jobId}/commits/{sha}/include", (
            string jobId, string sha, string? watchPath, IncludeCommitRequest? req,
            TaskScannerService scanner, TaskMutationService mutations, GitService git) =>
        {
            var info = scanner.FindJob(jobId, watchPath);
            if (info == null) return Results.NotFound(new { error = "Job not found" });

            // Enrich an add-from-recent pick with live git metadata so the
            // stored entry carries a real file count + subject, not just a SHA.
            TaskCommitInfo? candidate = null;
            var files = git.GetCommitFiles(jobId, watchPath, sha);
            if (files.Count > 0)
            {
                candidate = new TaskCommitInfo
                {
                    Sha = sha,
                    ShortSha = sha.Length > 8 ? sha[..8] : sha,
                    Message = req?.Message ?? "",
                    FilesChanged = files.Count,
                    Files = files.Select(f => f.Path).ToList(),
                    At = req?.At ?? DateTime.UtcNow,
                };
            }
            return mutations.IncludeCommit(jobId, sha, candidate, watchPath)
                ? Results.Ok(new { sha, included = true })
                : Results.BadRequest(new { error = "Could not include commit." });
        });

        group.MapPost("/{jobId}/open-in-vscode", (string jobId, string? watchPath, GitService git) =>
        {
            return git.OpenInVsCode(jobId, watchPath, out var error)
                ? Results.Ok()
                : Results.BadRequest(new { error });
        });
    }

    private static bool IsKnownJobCommit(
        TaskInfo info, TaskSessionLog sessions,
        string jobId, string? watchPath, GitService git, string sha)
    {
        if (string.IsNullOrWhiteSpace(sha)) return false;
        if (info.Commit != null && string.Equals(info.Commit.Sha, sha, StringComparison.OrdinalIgnoreCase))
            return true;
        var events = sessions.ReadSessionEvents(jobId, watchPath);
        var lines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(info.FolderPath));
        var timeline = RunTimelineBuilder.Build(events, lines, DateTime.UtcNow);
        var aggregate = TaskCommitsAggregator.Aggregate(info, timeline.Runs,
            (before, after) => git.GetCommitsInShaRange(jobId, watchPath, before, after));
        return aggregate.Commits.Any(c => string.Equals(c.Sha, sha, StringComparison.OrdinalIgnoreCase));
    }
}
