using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentStudio.Registry;

/// <summary>
/// Owns the dev-server processes launched for project URLs. Sessions remain
/// observable after the initiating request finishes, can be stopped explicitly,
/// and are terminated with the backend host so a preview cannot become an
/// orphaned child process.
/// </summary>
public sealed class ProjectUrlProcessService : IDisposable
{
    private const int MaxOutputLines = 1000;
    private readonly ILogger<ProjectUrlProcessService> _logger;
    private readonly ConcurrentDictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private readonly object _lifecycleGate = new();
    private int _disposed;

    public ProjectUrlProcessService(ILogger<ProjectUrlProcessService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Start or restart the owned server for <paramref name="url"/>. The
    /// command runs through the platform shell in the URL working directory,
    /// falling back to the project's repository path and then root path.
    /// </summary>
    public ProjectUrlProcessSnapshot Start(ProjectRecord project, ProjectUrlRecord url)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(url);
        var rule = url.StartRule;
        if (rule == null || string.IsNullOrWhiteSpace(rule.Command))
            throw new ArgumentException("URL has no start command to run.", nameof(url));

        var cwd = ResolveWorkingDirectory(project, rule);
        var key = Key(project.Id, url.Id);

        lock (_lifecycleGate)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            RetirePrevious(key);

            var process = new Process
            {
                StartInfo = BuildStartInfo(rule.Command, cwd),
                EnableRaisingEvents = true,
            };
            var session = new Session(project.Id, url.Id, rule.Command, cwd, process);
            process.OutputDataReceived += (_, eventArgs) => AppendOutput(session, eventArgs.Data, isError: false);
            process.ErrorDataReceived += (_, eventArgs) => AppendOutput(session, eventArgs.Data, isError: true);
            process.Exited += (_, _) => MarkExited(session);

            try
            {
                if (!process.Start())
                    throw new InvalidOperationException("Process.Start returned false.");

                session.ProcessId = process.Id;
                _sessions[key] = session;
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.StandardInput.Close();

                lock (session.Gate)
                {
                    if (session.State == ProjectUrlProcessStates.Starting && !process.HasExited)
                        session.State = ProjectUrlProcessStates.Running;
                    AppendOutputLocked(session, $"[studio] Started process {session.ProcessId}.");
                }

                if (process.HasExited)
                    MarkExited(session);

                _logger.LogInformation(
                    "project-url-started project={Id} url={UrlId} pid={Pid} command={Command} cwd={Cwd}",
                    project.Id, url.Id, session.ProcessId, rule.Command, cwd);
                return Snapshot(session);
            }
            catch (Exception ex)
            {
                _sessions.TryRemove(key, out _);
                TryKillAndDispose(process);
                _logger.LogError(ex,
                    "project-url-start-failed project={Id} url={UrlId} command={Command} cwd={Cwd}",
                    project.Id, url.Id, rule.Command, cwd);
                throw new InvalidOperationException($"Failed to start dev server: {ex.Message}", ex);
            }
        }
    }

    public ProjectUrlProcessSnapshot? Get(string projectId, string urlId)
        => _sessions.TryGetValue(Key(projectId, urlId), out var session)
            ? Snapshot(session)
            : null;

    public ProjectUrlProcessSnapshot? Stop(string projectId, string urlId)
    {
        lock (_lifecycleGate)
        {
            if (!_sessions.TryGetValue(Key(projectId, urlId), out var session)) return null;
            StopSession(session, "stopped by operator");
            return Snapshot(session);
        }
    }

    /// <summary>Stop every preview process owned by a project before it is deleted.</summary>
    public IReadOnlyList<ProjectUrlProcessSnapshot> StopProject(string projectId)
    {
        lock (_lifecycleGate)
        {
            var sessions = _sessions.Values
                .Where(session => string.Equals(session.ProjectId, projectId, StringComparison.Ordinal))
                .ToArray();
            foreach (var session in sessions)
                StopSession(session, "project removed");
            return sessions.Select(Snapshot).ToArray();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        lock (_lifecycleGate)
        {
            foreach (var session in _sessions.Values)
            {
                StopSession(session, "backend shutdown");
                session.Process.Dispose();
            }
        }
    }

    /// <summary>Resolve only from explicit URL configuration or project source roots.</summary>
    public static string ResolveWorkingDirectory(ProjectRecord project, ProjectUrlStartRule rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Cwd))
        {
            if (!Directory.Exists(rule.Cwd))
                throw new InvalidOperationException($"Working directory does not exist: {rule.Cwd}");
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(rule.Cwd));
        }

        foreach (var candidate in new[] { project.RepositoryPath, project.RootPath })
            if (!string.IsNullOrWhiteSpace(candidate) && Directory.Exists(candidate))
                return Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));

        var configured = project.RepositoryPath ?? project.RootPath;
        throw new InvalidOperationException(configured == null
            ? "No working directory is configured. Set a URL cwd or project repository/root path."
            : $"Working directory does not exist: {configured}");
    }

    internal static ProcessStartInfo BuildStartInfo(string command, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.FileName = "cmd.exe";
            psi.ArgumentList.Add("/c");
            psi.ArgumentList.Add(command);
        }
        else
        {
            psi.FileName = "/bin/sh";
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command);
        }
        return psi;
    }

    private void RetirePrevious(string key)
    {
        if (!_sessions.TryRemove(key, out var previous)) return;
        StopSession(previous, "restarted");
        previous.Process.Dispose();
    }

    private void StopSession(Session session, string reason)
    {
        lock (session.Gate)
        {
            if (!ProjectUrlProcessStates.IsActive(session.State)) return;
            try
            {
                if (!session.Process.HasExited)
                    session.Process.Kill(entireProcessTree: true);
                if (!session.Process.WaitForExit(5000))
                    throw new InvalidOperationException("Process did not stop within five seconds.");

                session.State = ProjectUrlProcessStates.Stopped;
                session.FinishedAtUtc = DateTimeOffset.UtcNow;
                session.ExitCode = TryGetExitCode(session.Process);
                AppendOutputLocked(session, $"[studio] Process {reason}.");
                _logger.LogInformation(
                    "project-url-stopped project={ProjectId} url={UrlId} pid={Pid} reason={Reason}",
                    session.ProjectId, session.UrlId, session.ProcessId, reason);
            }
            catch (Exception ex)
            {
                session.State = ProjectUrlProcessStates.Failed;
                session.FinishedAtUtc = DateTimeOffset.UtcNow;
                AppendOutputLocked(session, $"[studio] Stop failed: {ex.Message}");
                _logger.LogWarning(ex,
                    "project-url-stop-failed project={ProjectId} url={UrlId} pid={Pid} reason={Reason}",
                    session.ProjectId, session.UrlId, session.ProcessId, reason);
            }
        }
    }

    private void MarkExited(Session session)
    {
        lock (session.Gate)
        {
            if (!ProjectUrlProcessStates.IsActive(session.State)) return;
            session.State = ProjectUrlProcessStates.Exited;
            session.FinishedAtUtc = DateTimeOffset.UtcNow;
            session.ExitCode = TryGetExitCode(session.Process);
            AppendOutputLocked(session,
                $"[studio] Process exited with code {session.ExitCode?.ToString() ?? "unknown"}.");
            _logger.LogInformation(
                "project-url-exited project={ProjectId} url={UrlId} pid={Pid} exitCode={ExitCode}",
                session.ProjectId, session.UrlId, session.ProcessId, session.ExitCode);
        }
    }

    private void AppendOutput(Session session, string? line, bool isError)
    {
        if (line == null) return;
        lock (session.Gate) AppendOutputLocked(session, line);
        if (isError)
            _logger.LogWarning("project-url-error project={ProjectId} url={UrlId} text={Text}",
                session.ProjectId, session.UrlId, line);
        else
            _logger.LogDebug("project-url-output project={ProjectId} url={UrlId} text={Text}",
                session.ProjectId, session.UrlId, line);
    }

    private static void AppendOutputLocked(Session session, string line)
    {
        session.Output.Add(line);
        if (session.Output.Count > MaxOutputLines)
            session.Output.RemoveRange(0, session.Output.Count - MaxOutputLines);
    }

    private static ProjectUrlProcessSnapshot Snapshot(Session session)
    {
        lock (session.Gate)
        {
            return new ProjectUrlProcessSnapshot(
                session.ProjectId,
                session.UrlId,
                session.Command,
                session.Cwd,
                session.State,
                session.ProcessId,
                session.StartedAtUtc,
                session.FinishedAtUtc,
                session.ExitCode,
                [.. session.Output]);
        }
    }

    private static int? TryGetExitCode(Process process)
    {
        try { return process.HasExited ? process.ExitCode : null; }
        catch (InvalidOperationException) { return null; }
    }

    private void TryKillAndDispose(Process process)
    {
        try
        {
            if (process.Id > 0 && !process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "project-url-failed-launch-cleanup-failed");
        }
        finally { process.Dispose(); }
    }

    private static string Key(string projectId, string urlId) => $"{projectId}::{urlId}";

    private sealed class Session(
        string projectId,
        string urlId,
        string command,
        string cwd,
        Process process)
    {
        public object Gate { get; } = new();
        public string ProjectId { get; } = projectId;
        public string UrlId { get; } = urlId;
        public string Command { get; } = command;
        public string Cwd { get; } = cwd;
        public Process Process { get; } = process;
        public int ProcessId { get; set; }
        public DateTimeOffset StartedAtUtc { get; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? FinishedAtUtc { get; set; }
        public int? ExitCode { get; set; }
        public string State { get; set; } = ProjectUrlProcessStates.Starting;
        public List<string> Output { get; } = [];
    }
}

public static class ProjectUrlProcessStates
{
    public const string Starting = "starting";
    public const string Running = "running";
    public const string Exited = "exited";
    public const string Stopped = "stopped";
    public const string Failed = "failed";

    public static bool IsActive(string state) => state is Starting or Running;
}

public sealed record ProjectUrlProcessSnapshot(
    string ProjectId,
    string UrlId,
    string Command,
    string Cwd,
    string State,
    int ProcessId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset? FinishedAtUtc,
    int? ExitCode,
    IReadOnlyList<string> Output)
{
    /// <summary>Compatibility marker retained for existing start callers.</summary>
    public bool Started => ProcessId > 0;
}
