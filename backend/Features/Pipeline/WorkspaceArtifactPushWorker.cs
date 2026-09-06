using System.Collections.Concurrent;
using System.Diagnostics;
using AgentStudio.Git;
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
    private readonly AgentStudio.Bus.AgentMessageBusBridge? _bus;
    private readonly TimeSpan _baseBackoff;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _catchUpTimeout;
    private readonly int _backlogWarningCommitCount;
    private readonly long _backlogWarningBytes;
    private readonly Func<WorkspaceGitInvocation, CancellationToken, Task<GitProcessResult>> _runGit;

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> PushGates =
        new(StringComparer.OrdinalIgnoreCase);

    public WorkspaceArtifactPushWorker(
        WorkspaceArtifactPushQueue queue,
        ILogger<WorkspaceArtifactPushWorker> logger,
        IConfiguration configuration,
        AgentStudio.Bus.AgentMessageBusBridge? bus = null)
        : this(queue, logger, configuration, bus, RunGitAsync)
    {
    }

    internal WorkspaceArtifactPushWorker(
        WorkspaceArtifactPushQueue queue,
        ILogger<WorkspaceArtifactPushWorker> logger,
        IConfiguration configuration,
        AgentStudio.Bus.AgentMessageBusBridge? bus,
        Func<WorkspaceGitInvocation, CancellationToken, Task<GitProcessResult>> runGit)
    {
        _queue = queue;
        _logger = logger;
        _bus = bus;
        _baseBackoff = TimeSpan.FromSeconds(Math.Max(0,
            configuration.GetValue<int?>("WorkspaceArtifacts:PushRetrySeconds") ?? 30));
        _defaultTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("WorkspaceArtifacts:PushTimeoutSeconds") ?? 30, 1, 3600));
        _catchUpTimeout = TimeSpan.FromSeconds(Math.Clamp(
            configuration.GetValue<int?>("WorkspaceArtifacts:CatchUpPushTimeoutSeconds") ?? 600, 1, 3600));
        _backlogWarningCommitCount = Math.Clamp(
            configuration.GetValue<int?>("WorkspaceArtifacts:BacklogWarningCommitCount") ?? 50, 1, 100_000);
        _backlogWarningBytes = Math.Clamp(
            configuration.GetValue<long?>("WorkspaceArtifacts:BacklogWarningBytes") ?? 100L * 1024 * 1024,
            1L * 1024 * 1024,
            10L * 1024 * 1024 * 1024);
        _runGit = runGit;
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
        var gate = PushGates.GetOrAdd(
            Path.GetFullPath(request.RepositoryRoot),
            _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var backlog = await ReadBacklogAsync(request, ct).ConfigureAwait(false);
            if (WorkspacePushRetryPolicy.ShouldWarn(
                    backlog.AheadCount,
                    backlog.EstimatedBytes,
                    _backlogWarningCommitCount,
                    _backlogWarningBytes))
            {
                _logger.LogWarning(
                    "workspace-artifact-push-backlog repo={Repo} branch={Branch} ahead={AheadCount} estimatedBytes={EstimatedBytes} warningCommitCount={WarningCommitCount} warningBytes={WarningBytes}",
                    request.RepositoryRoot,
                    request.TargetBranch,
                    backlog.AheadCount,
                    backlog.EstimatedBytes,
                    _backlogWarningCommitCount,
                    _backlogWarningBytes);
            }

            var catchUpMode = false;
            GitProcessResult? last = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                var timeout = WorkspacePushRetryPolicy.TimeoutForAttempt(
                    attempt, catchUpMode, _defaultTimeout, _catchUpTimeout);
                last = await _runGit(
                    new WorkspaceGitInvocation(
                        request.RepositoryRoot,
                        ["push", "origin", $"{request.Sha ?? "HEAD"}:refs/heads/{request.TargetBranch}"],
                        timeout),
                    ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (last.Success)
                {
                    _logger.LogInformation(
                        "workspace-artifact-push-succeeded jobId={JobId} repo={Repo} attempt={Attempt} aheadBeforePush={AheadCount}",
                        request.JobId, request.RepositoryRoot, attempt, backlog.AheadCount);
                    return true;
                }

                catchUpMode |= last.FailureKind == GitProcessFailureKind.TimedOut;
                _logger.LogWarning(
                    "workspace-artifact-push-failed jobId={JobId} repo={Repo} attempt={Attempt} timeoutSeconds={TimeoutSeconds} failureKind={FailureKind} ahead={AheadCount} error={Error}",
                    request.JobId,
                    request.RepositoryRoot,
                    attempt,
                    timeout.TotalSeconds,
                    last.FailureKind,
                    backlog.AheadCount,
                    ErrorText(last));
                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromTicks(_baseBackoff.Ticks * (1L << (attempt - 1))), ct);
            }

            if (_bus != null)
            {
                var ahead = backlog.AheadCount ?? 0;
                await _bus.EmitAdvisoryAsync(
                    new SupervisorAdvisory(
                        DateTime.UtcNow,
                        string.IsNullOrWhiteSpace(request.Project) ? "workspace" : request.Project,
                        SupervisorSeverity.High,
                        SupervisorSource.HardCheck,
                        "workspace-repository-push-blocked",
                        $"Workspace repository push exhausted 3 attempts for '{request.RepositoryRoot}' " +
                        $"to '{request.TargetBranch}'. Ahead count: {ahead}. Last error: {ErrorText(last!)}",
                        request.JobId),
                    ct).ConfigureAwait(false);
            }
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<WorkspaceRepositoryBacklog> ReadBacklogAsync(
        WorkspaceArtifactPushRequest request,
        CancellationToken ct)
    {
        var subject = request.Sha ?? "HEAD";
        var upstream = $"refs/remotes/origin/{request.TargetBranch}";
        var range = $"{upstream}..{subject}";
        var count = await _runGit(
            new WorkspaceGitInvocation(
                request.RepositoryRoot,
                ["rev-list", "--count", range],
                _defaultTimeout),
            ct).ConfigureAwait(false);
        if (!count.Success)
        {
            count = await _runGit(
                new WorkspaceGitInvocation(
                    request.RepositoryRoot,
                    ["rev-list", "--count", subject],
                    _defaultTimeout),
                ct).ConfigureAwait(false);
            range = subject;
        }

        var size = await _runGit(
            new WorkspaceGitInvocation(
                request.RepositoryRoot,
                ["rev-list", "--disk-usage", range],
                _defaultTimeout),
            ct).ConfigureAwait(false);
        return new WorkspaceRepositoryBacklog(
            ParseLong(count.StandardOutput),
            ParseLong(size.StandardOutput));
    }

    private static long? ParseLong(string value) =>
        long.TryParse(value.Trim(), out var parsed) ? parsed : null;

    private static string ErrorText(GitProcessResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();

    private static async Task<GitProcessResult> RunGitAsync(
        WorkspaceGitInvocation invocation,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = invocation.RepositoryRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in invocation.Arguments) psi.ArgumentList.Add(arg);
        return await GitNetworkProcessRunner.RunAsync(
            psi,
            stdin: null,
            invocation.Timeout,
            ct).ConfigureAwait(false);
    }
}

internal sealed record WorkspaceGitInvocation(
    string RepositoryRoot,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout);

internal sealed record WorkspaceRepositoryBacklog(long? AheadCount, long? EstimatedBytes);

internal static class WorkspacePushRetryPolicy
{
    internal static TimeSpan TimeoutForAttempt(
        int attempt,
        bool catchUpMode,
        TimeSpan defaultTimeout,
        TimeSpan catchUpTimeout) =>
        attempt > 1 && catchUpMode ? catchUpTimeout : defaultTimeout;

    internal static bool ShouldWarn(
        long? aheadCount,
        long? estimatedBytes,
        int commitThreshold,
        long byteThreshold) =>
        aheadCount >= commitThreshold || estimatedBytes >= byteThreshold;
}
