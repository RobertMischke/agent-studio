using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentStudio.Registry;

/// <summary>
/// Spawns the dev-server process behind a <see cref="ProjectUrlRecord.StartRule"/>.
/// Intentionally minimal for v1: it launches the command in its working
/// directory and tracks the resulting process so a subsequent "restart" can
/// stop the previous one first. It does NOT supervise the process, capture its
/// stdout/stderr, or infer running state - liveness is decided by an HTTP probe
/// against the URL (see the frontend probe), so an externally started server
/// still reports as running. Surfacing process output in a console drawer is
/// explicitly future scope.
/// </summary>
public sealed class ProjectUrlProcessService
{
    private readonly ILogger<ProjectUrlProcessService> _logger;
    private readonly ConcurrentDictionary<string, Process> _running = new(StringComparer.Ordinal);

    public ProjectUrlProcessService(ILogger<ProjectUrlProcessService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Build &amp; start (or restart) the server for <paramref name="url"/>. The
    /// command runs through the platform shell in <see cref="ProjectUrlStartRule.Cwd"/>,
    /// defaulting to the project's <see cref="ProjectRecord.RepositoryPath"/>.
    /// Throws <see cref="ArgumentException"/> when there is no command, and
    /// <see cref="InvalidOperationException"/> when the working directory is
    /// missing or the process fails to launch.
    /// </summary>
    public void Start(ProjectRecord project, ProjectUrlRecord url)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(url);
        var rule = url.StartRule;
        if (rule == null || string.IsNullOrWhiteSpace(rule.Command))
            throw new ArgumentException("URL has no start command to run.", nameof(url));

        var cwd = string.IsNullOrWhiteSpace(rule.Cwd) ? project.RepositoryPath : rule.Cwd;
        if (string.IsNullOrWhiteSpace(cwd) || !Directory.Exists(cwd))
            throw new InvalidOperationException(
                $"Working directory does not exist: {cwd ?? "(none - project has no RepositoryPath)"}");

        var key = $"{project.Id}::{url.Id}";
        StopIfRunning(key);

        var psi = BuildStartInfo(rule.Command, cwd!);
        try
        {
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            _running[key] = process;
            _logger.LogInformation(
                "project-url-started project={Id} url={UrlId} pid={Pid} command={Command} cwd={Cwd}",
                project.Id, url.Id, process.Id, rule.Command, cwd);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex,
                "project-url-start-failed project={Id} url={UrlId} command={Command} cwd={Cwd}",
                project.Id, url.Id, rule.Command, cwd);
            throw new InvalidOperationException($"Failed to start dev server: {ex.Message}", ex);
        }
    }

    private void StopIfRunning(string key)
    {
        if (!_running.TryRemove(key, out var previous)) return;
        try
        {
            if (!previous.HasExited)
            {
                previous.Kill(entireProcessTree: true);
                _logger.LogInformation("project-url-stopped-previous key={Key} pid={Pid}", key, previous.Id);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "project-url-stop-previous-failed key={Key}", key);
        }
        finally
        {
            previous.Dispose();
        }
    }

    private static ProcessStartInfo BuildStartInfo(string command, string cwd)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = cwd,
            UseShellExecute = false,
            CreateNoWindow = true,
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
}
