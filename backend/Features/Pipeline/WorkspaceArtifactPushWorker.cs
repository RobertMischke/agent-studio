using System.Collections.Concurrent;
using System.Diagnostics;
using AgentStudio.State;
using AgentStudio.Supervisor;

namespace AgentStudio.Pipeline;

/// <summary>
/// Pushes workspace artifacts and other bounded platform-owned repository
/// commits off the run path.
/// Failures are visible and retried with bounded exponential backoff; a later
/// commit also acts as a natural retry for all still-ahead commits.
/// </summary>
public sealed class WorkspaceArtifactPushWorker : BackgroundService
{
    private readonly WorkspaceArtifactPushQueue _queue;
    private readonly ILogger<WorkspaceArtifactPushWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly AgentStudio.Bus.AgentMessageBusBridge? _bus;
    private readonly SupervisorAdvisoryStore? _advisories;
    private readonly TimeSpan _baseBackoff;
    private readonly TimeSpan _catchUpTimeout;
    private readonly int _backlogWarningCommits;
    private readonly long _backlogWarningBytes;
    private readonly TimeProvider _time;
    private readonly Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<WorkspacePushGitResult>> _runGit;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RepoPushGates =
        new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceArtifactPushWorker(
        WorkspaceArtifactPushQueue queue,
        ILogger<WorkspaceArtifactPushWorker> logger,
        IConfiguration configuration,
        AgentStudio.Bus.AgentMessageBusBridge? bus = null,
        SupervisorAdvisoryStore? advisories = null,
        TimeProvider? time = null)
        : this(queue, logger, configuration, bus, advisories, time, RunGitAsync)
    {
    }

    internal WorkspaceArtifactPushWorker(
        WorkspaceArtifactPushQueue queue,
        ILogger<WorkspaceArtifactPushWorker> logger,
        IConfiguration configuration,
        AgentStudio.Bus.AgentMessageBusBridge? bus,
        SupervisorAdvisoryStore? advisories,
        TimeProvider? time,
        Func<string, IReadOnlyList<string>, TimeSpan, CancellationToken, Task<WorkspacePushGitResult>> runGit)
    {
        _queue = queue;
        _logger = logger;
        _configuration = configuration;
        _bus = bus;
        _advisories = advisories;
        _time = time ?? TimeProvider.System;
        _runGit = runGit;
        _baseBackoff = TimeSpan.FromSeconds(Math.Max(0,
            configuration.GetValue<int?>("WorkspaceArtifacts:PushRetrySeconds") ?? 30));
        _catchUpTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("WorkspaceArtifacts:CatchUpTimeoutSeconds") ?? 600,
            30, 3600));
        _backlogWarningCommits = Math.Clamp(
            configuration.GetValue<int?>("WorkspaceArtifacts:BacklogWarningCommits") ?? 50,
            1, 100_000);
        _backlogWarningBytes = Math.Max(1,
            configuration.GetValue<long?>("WorkspaceArtifacts:BacklogWarningBytes") ?? 512L * 1024 * 1024);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(stoppingToken))
                await ProcessAsync(request, stoppingToken);
        }
        catch (OperationCanceledException ex) when (stoppingToken.IsCancellationRequested)
        {
            SilentCatch.Note(ex, "WorkspaceArtifactPushWorker: graceful shutdown; a later workspace commit retries pending history.");
        }
    }

    internal async Task<bool> ProcessAsync(WorkspaceArtifactPushRequest request, CancellationToken ct)
    {
        var gate = RepoPushGates.GetOrAdd(
            Path.GetFullPath(request.RepositoryRoot), _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await ProcessSingleFlightAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> ProcessSingleFlightAsync(WorkspaceArtifactPushRequest request, CancellationToken ct)
    {
        var backlog = await MeasureBacklogAsync(request, ct).ConfigureAwait(false);
        if (backlog.AheadCount >= _backlogWarningCommits || backlog.EstimatedBytes >= _backlogWarningBytes)
        {
            _logger.LogWarning(
                "workspace-artifact-push-backlog repo={Repo} branch={Branch} ahead={AheadCount} estimatedBytes={EstimatedBytes}",
                request.RepositoryRoot, request.TargetBranch, backlog.AheadCount, backlog.EstimatedBytes);
        }

        var timeout = GitNetworkProcessRunner.DefaultTimeout;
        WorkspacePushGitResult result = default;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            result = await _runGit(request.RepositoryRoot,
                ["push", "origin", $"{request.Sha ?? "HEAD"}:refs/heads/{request.TargetBranch}"], timeout, ct);
            if (result.Code == 0)
            {
                _logger.LogInformation(
                    "workspace-artifact-push-succeeded jobId={JobId} repo={Repo} attempt={Attempt}",
                    request.JobId, request.RepositoryRoot, attempt);
                return true;
            }

            if (result.TimedOut)
                timeout = _catchUpTimeout;

            _logger.LogWarning(
                "workspace-artifact-push-failed jobId={JobId} repo={Repo} attempt={Attempt} error={Error}",
                request.JobId, request.RepositoryRoot, attempt, result.Error);
            if (attempt < 3)
                await Task.Delay(TimeSpan.FromTicks(_baseBackoff.Ticks * (1L << (attempt - 1))), ct);
            else
                await ReportExhaustedAsync(request, backlog.AheadCount, result.Error, attempt, ct).ConfigureAwait(false);
        }
        return false;
    }

    private async Task<WorkspacePushBacklog> MeasureBacklogAsync(
        WorkspaceArtifactPushRequest request, CancellationToken ct)
    {
        var target = request.Sha ?? "HEAD";
        var aheadResult = await _runGit(
            request.RepositoryRoot,
            ["rev-list", "--count", $"origin/{request.TargetBranch}..{target}"],
            GitNetworkProcessRunner.DefaultTimeout,
            ct).ConfigureAwait(false);
        _ = int.TryParse(aheadResult.Output.Trim(), out var ahead);

        var objects = await _runGit(
            request.RepositoryRoot,
            ["count-objects", "-v"],
            GitNetworkProcessRunner.DefaultTimeout,
            ct).ConfigureAwait(false);
        long kib = 0;
        if (objects.Code == 0)
        {
            foreach (var line in objects.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.Split(' ', 2, StringSplitOptions.TrimEntries);
                if (parts.Length == 2
                    && parts[0] is "size:" or "size-pack:"
                    && long.TryParse(parts[1], out var value))
                    kib += value;
            }
        }
        return new WorkspacePushBacklog(Math.Max(0, ahead), kib * 1024);
    }

    private async Task ReportExhaustedAsync(
        WorkspaceArtifactPushRequest request,
        int aheadCount,
        string error,
        int attempts,
        CancellationToken ct)
    {
        if (_bus != null)
        {
            try
            {
                await _bus.EmitManagedRepoPushFailureAsync(
                    project: request.Project,
                    jobId: request.JobId,
                    repository: request.RepositoryRoot,
                    branch: request.TargetBranch,
                    status: "failed",
                    error: error,
                    attempts: attempts,
                    ct: ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "workspace-artifact-push final bus report failed repo={Repo}", request.RepositoryRoot);
            }
        }

        if (_advisories == null) return;
        var workspace = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(workspace)) return;
        var project = string.IsNullOrWhiteSpace(request.Project) ? "_workspace" : request.Project!;
        try
        {
            await _advisories.AppendAsync(workspace, project, new SupervisorAdvisory(
                _time.GetUtcNow().UtcDateTime,
                project,
                SupervisorSeverity.High,
                SupervisorSource.HardCheck,
                "workspace-repository-push-backlog",
                $"Workspace repository push failed after {attempts} attempts. Repository: {request.RepositoryRoot}. Ahead commits: {aheadCount}. Error: {error}",
                request.JobId), ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "workspace-artifact-push final supervisor advisory failed repo={Repo}", request.RepositoryRoot);
        }
    }

    private static async Task<WorkspacePushGitResult> RunGitAsync(
        string cwd, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        var result = await GitNetworkProcessRunner.RunAsync(
            psi,
            stdin: null,
            timeout,
            ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return new WorkspacePushGitResult(
            result.ExitCode,
            result.StandardOutput,
            result.StandardError.Trim(),
            result.FailureKind == GitProcessFailureKind.TimedOut);
    }

    private sealed record WorkspacePushBacklog(int AheadCount, long EstimatedBytes);
}

internal readonly record struct WorkspacePushGitResult(
    int Code,
    string Output,
    string Error,
    bool TimedOut);
