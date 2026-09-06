using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentStudio.Git;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>
/// Runs bounded Git object maintenance for the TaskRepository. The backend
/// owns the timer so work is admitted through the same host-load gate as other
/// expensive operations instead of installing an unmanaged OS scheduler.
/// </summary>
public sealed class WorkspaceRepositoryMaintenanceService : BackgroundService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<WorkspaceRepositoryMaintenanceService> _logger;
    private readonly ILoadThrottleGate? _loadGate;

    public WorkspaceRepositoryMaintenanceService(
        IConfiguration configuration,
        ILogger<WorkspaceRepositoryMaintenanceService> logger,
        ILoadThrottleGate? loadGate = null)
    {
        _configuration = configuration;
        _logger = logger;
        _loadGate = loadGate;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (_loadGate != null)
                    await _loadGate.WaitUntilReadyAsync("workspace-repository-maintenance", stoppingToken)
                        .ConfigureAwait(false);
                var result = RunOnce(stoppingToken);
                if (!result.Success)
                    _logger.LogWarning(
                        "workspace-repository-maintenance-failed repo={Repo} phase={Phase} error={Error}",
                        result.RepositoryRoot,
                        result.Phase,
                        result.Error);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "workspace-repository-maintenance tick failed");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    internal WorkspaceRepositoryMaintenanceResult RunOnce(CancellationToken ct = default)
    {
        var configuredRoot = _configuration["TaskRepository"];
        if (string.IsNullOrWhiteSpace(configuredRoot) || !Directory.Exists(configuredRoot))
            return WorkspaceRepositoryMaintenanceResult.Skipped(configuredRoot, "workspace-missing");

        var resolved = RunGit(configuredRoot, ["rev-parse", "--show-toplevel"], DefaultCommandTimeout, ct);
        if (!resolved.Success || string.IsNullOrWhiteSpace(resolved.StandardOutput))
            return WorkspaceRepositoryMaintenanceResult.Failed(configuredRoot, "resolve-root", ErrorText(resolved));
        var gitRoot = Path.GetFullPath(resolved.StandardOutput.Trim());

        lock (WorkspaceArtifactCommitService.RepositoryGate(gitRoot))
        {
            var configured = ConfigureRepository(gitRoot, ct);
            if (!configured.Success)
                return WorkspaceRepositoryMaintenanceResult.Failed(gitRoot, "configure", ErrorText(configured));

            var exclude = EnsureAttemptAuthorityExclude(gitRoot, ct);
            if (!exclude.Success)
                return WorkspaceRepositoryMaintenanceResult.Failed(gitRoot, "runtime-exclude", ErrorText(exclude));

            var before = ReadLooseObjectCount(gitRoot, ct);
            if (before is null)
                return WorkspaceRepositoryMaintenanceResult.Failed(gitRoot, "count-objects", "Could not read loose object count.");

            var loose = before.Value;
            var passes = 0;
            while ((loose > LooseObjectLimit || passes == 0 && loose > 0)
                   && passes < MaxLooseObjectPasses)
            {
                passes++;
                var consolidate = RunGit(
                    gitRoot,
                    ["maintenance", "run", "--task=loose-objects"],
                    MaintenanceTimeout,
                    ct);
                if (!consolidate.Success)
                    return WorkspaceRepositoryMaintenanceResult.Failed(
                        gitRoot,
                        "loose-objects",
                        ErrorText(consolidate),
                        before,
                        loose,
                        passes);
                loose = ReadLooseObjectCount(gitRoot, ct) ?? loose;
            }

            if (before > 0)
            {
                var repack = RunGit(
                    gitRoot,
                    ["maintenance", "run", "--task=incremental-repack"],
                    MaintenanceTimeout,
                    ct);
                if (!repack.Success)
                    return WorkspaceRepositoryMaintenanceResult.Failed(
                        gitRoot,
                        "incremental-repack",
                        ErrorText(repack),
                        before,
                        loose,
                        passes);
            }

            var after = ReadLooseObjectCount(gitRoot, ct) ?? loose;
            if (after > LooseObjectLimit)
            {
                _logger.LogWarning(
                    "workspace-repository-maintenance-loose-object-bound-not-reached repo={Repo} looseBefore={LooseBefore} looseAfter={LooseAfter} bound={Bound} passes={Passes}",
                    gitRoot,
                    before,
                    after,
                    LooseObjectLimit,
                    passes);
            }
            else
            {
                _logger.LogInformation(
                    "workspace-repository-maintenance-succeeded repo={Repo} looseBefore={LooseBefore} looseAfter={LooseAfter} bound={Bound} passes={Passes}",
                    gitRoot,
                    before,
                    after,
                    LooseObjectLimit,
                    passes);
            }
            return WorkspaceRepositoryMaintenanceResult.Completed(gitRoot, before.Value, after, passes);
        }
    }

    private GitProcessResult ConfigureRepository(string gitRoot, CancellationToken ct)
    {
        var settings = new[]
        {
            ("gc.auto", LooseObjectLimit.ToString()),
            ("maintenance.strategy", "incremental"),
            ("maintenance.loose-objects.batchSize", LooseObjectBatchSize.ToString()),
        };
        foreach (var (key, value) in settings)
        {
            var result = RunGit(gitRoot, ["config", "--local", key, value], DefaultCommandTimeout, ct);
            if (!result.Success) return result;
        }

        if (!OperatingSystem.IsWindows()) return SuccessfulGitResult();

        var version = RunGit(gitRoot, ["version"], DefaultCommandTimeout, ct);
        if (!version.Success) return version;
        if (!WorkspaceRepositoryMaintenancePolicy.SupportsBuiltInFsMonitor(version.StandardOutput))
        {
            _logger.LogWarning(
                "workspace-repository-fsmonitor-disabled repo={Repo} gitVersion={GitVersion} reason=unsupported-version",
                gitRoot,
                version.StandardOutput.Trim());
            return SuccessfulGitResult();
        }
        var fsMonitor = RunGit(
            gitRoot,
            ["fsmonitor--daemon", "start"],
            DefaultCommandTimeout,
            ct);
        if (!fsMonitor.Success)
        {
            _logger.LogWarning(
                "workspace-repository-fsmonitor-disabled repo={Repo} gitVersion={GitVersion} reason=daemon-start-failed error={Error}",
                gitRoot,
                version.StandardOutput.Trim(),
                ErrorText(fsMonitor));
            return SuccessfulGitResult();
        }
        return RunGit(gitRoot, ["config", "--local", "core.fsmonitor", "true"], DefaultCommandTimeout, ct);
    }

    private GitProcessResult EnsureAttemptAuthorityExclude(string gitRoot, CancellationToken ct)
    {
        var gitPath = RunGit(gitRoot, ["rev-parse", "--git-path", "info/exclude"], DefaultCommandTimeout, ct);
        if (!gitPath.Success) return gitPath;
        var rawPath = gitPath.StandardOutput.Trim();
        var excludePath = Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.GetFullPath(Path.Combine(gitRoot, rawPath));
        var directory = Path.GetDirectoryName(excludePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);

        const string pattern = ".metadata/attempt-authority*";
        var lines = File.Exists(excludePath)
            ? File.ReadAllLines(excludePath)
            : [];
        if (!lines.Any(line => string.Equals(line.Trim(), pattern, StringComparison.Ordinal)))
        {
            var separator = File.Exists(excludePath)
                            && new FileInfo(excludePath).Length > 0
                            && !File.ReadAllText(excludePath).EndsWith('\n')
                ? Environment.NewLine
                : string.Empty;
            File.AppendAllText(excludePath, separator + pattern + Environment.NewLine);
        }
        return SuccessfulGitResult();
    }

    private int? ReadLooseObjectCount(string gitRoot, CancellationToken ct)
    {
        var count = RunGit(gitRoot, ["count-objects", "-v"], DefaultCommandTimeout, ct);
        if (!count.Success) return null;
        foreach (var line in count.StandardOutput.Split('\n'))
        {
            if (line.StartsWith("count: ", StringComparison.Ordinal)
                && int.TryParse(line["count: ".Length..].Trim(), out var parsed))
                return parsed;
        }
        return null;
    }

    private bool Enabled =>
        _configuration.GetValue<bool?>("WorkspaceRepository:MaintenanceEnabled") ?? true;

    private TimeSpan Interval => TimeSpan.FromHours(Math.Clamp(
        _configuration.GetValue<int?>("WorkspaceRepository:MaintenanceIntervalHours") ?? 6,
        1,
        24 * 7));

    private int LooseObjectLimit => Math.Clamp(
        _configuration.GetValue<int?>("WorkspaceRepository:GcAutoLooseObjectLimit") ?? 10_000,
        100,
        1_000_000);

    private int LooseObjectBatchSize => Math.Clamp(
        _configuration.GetValue<int?>("WorkspaceRepository:LooseObjectBatchSize") ?? 50_000,
        100,
        1_000_000);

    private int MaxLooseObjectPasses => Math.Clamp(
        _configuration.GetValue<int?>("WorkspaceRepository:MaxLooseObjectPasses") ?? 12,
        1,
        100);

    private TimeSpan MaintenanceTimeout => TimeSpan.FromMinutes(Math.Clamp(
        _configuration.GetValue<int?>("WorkspaceRepository:MaintenanceTimeoutMinutes") ?? 30,
        1,
        120));

    private static TimeSpan DefaultCommandTimeout => TimeSpan.FromSeconds(30);

    private static GitProcessResult RunGit(
        string cwd,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        return GitNetworkProcessRunner.Run(start, timeout: timeout, cancellationToken: ct);
    }

    private static string ErrorText(GitProcessResult result) =>
        string.IsNullOrWhiteSpace(result.StandardError)
            ? result.StandardOutput.Trim()
            : result.StandardError.Trim();

    private static GitProcessResult SuccessfulGitResult() =>
        new(0, string.Empty, string.Empty, GitProcessFailureKind.None);
}

internal static partial class WorkspaceRepositoryMaintenancePolicy
{
    [GeneratedRegex(@"git version (?<major>\d+)\.(?<minor>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex GitVersionPattern();

    internal static bool SupportsBuiltInFsMonitor(string gitVersionOutput)
    {
        var match = GitVersionPattern().Match(gitVersionOutput ?? string.Empty);
        if (!match.Success
            || !int.TryParse(match.Groups["major"].Value, out var major)
            || !int.TryParse(match.Groups["minor"].Value, out var minor))
            return false;
        return major > 2 || major == 2 && minor >= 37;
    }
}

internal sealed record WorkspaceRepositoryMaintenanceResult(
    bool Success,
    bool DidRun,
    string? RepositoryRoot,
    string? Phase,
    string? Error,
    int? LooseObjectsBefore,
    int? LooseObjectsAfter,
    int Passes)
{
    internal static WorkspaceRepositoryMaintenanceResult Completed(
        string root,
        int before,
        int after,
        int passes) => new(true, true, root, null, null, before, after, passes);

    internal static WorkspaceRepositoryMaintenanceResult Skipped(string? root, string reason) =>
        new(true, false, root, reason, null, null, null, 0);

    internal static WorkspaceRepositoryMaintenanceResult Failed(
        string? root,
        string phase,
        string error,
        int? before = null,
        int? after = null,
        int passes = 0) => new(false, true, root, phase, error, before, after, passes);
}
