using System.Diagnostics;

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

    public WorkspaceArtifactPushWorker(
        WorkspaceArtifactPushQueue queue,
        ILogger<WorkspaceArtifactPushWorker> logger,
        IConfiguration configuration,
        AgentStudio.Bus.AgentMessageBusBridge? bus = null)
    {
        _queue = queue;
        _logger = logger;
        _bus = bus;
        _baseBackoff = TimeSpan.FromSeconds(Math.Max(0,
            configuration.GetValue<int?>("WorkspaceArtifacts:PushRetrySeconds") ?? 30));
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
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            var result = await RunGitAsync(request.RepositoryRoot,
                ["push", "origin", $"{request.Sha ?? "HEAD"}:refs/heads/{request.TargetBranch}"], ct);
            if (result.Code == 0)
            {
                _logger.LogInformation(
                    "workspace-artifact-push-succeeded jobId={JobId} repo={Repo} attempt={Attempt}",
                    request.JobId, request.RepositoryRoot, attempt);
                return true;
            }

            _logger.LogWarning(
                "workspace-artifact-push-failed jobId={JobId} repo={Repo} attempt={Attempt} error={Error}",
                request.JobId, request.RepositoryRoot, attempt, result.Error);
            if (attempt < 3)
                await Task.Delay(TimeSpan.FromTicks(_baseBackoff.Ticks * (1L << (attempt - 1))), ct);
            else if (_bus != null)
                await _bus.EmitManagedRepoPushFailureAsync(
                    project: request.Project,
                    jobId: request.JobId,
                    repository: request.RepositoryRoot,
                    branch: request.TargetBranch,
                    status: "failed",
                    error: result.Error,
                    attempts: attempt,
                    ct: ct);
        }
        return false;
    }

    private static async Task<(int Code, string Error)> RunGitAsync(
        string cwd, IReadOnlyList<string> args, CancellationToken ct)
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
            GitNetworkProcessRunner.DefaultTimeout,
            ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return (result.ExitCode, result.StandardError.Trim());
    }
}
