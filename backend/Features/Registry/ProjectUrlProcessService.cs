using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentStudio.Registry;

/// <summary>
/// Spawns the dev-server process behind a <see cref="ProjectUrlRecord.StartRule"/>.
/// Launches a configured command in its working directory and tracks the
/// resulting process so a subsequent restart can stop the previous one first.
/// Liveness is still decided by an HTTP probe against the configured URL.
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
    public ProjectUrlStartResult Start(ProjectRecord project, ProjectUrlRecord url)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(url);
        var rule = url.StartRule;
        if (rule == null || string.IsNullOrWhiteSpace(rule.Command))
            throw new ArgumentException("URL has no start command to run.", nameof(url));

        var cwd = ResolveWorkingDirectory(project, rule);

        var key = $"{project.Id}::{url.Id}";
        StopIfRunning(key);

        var psi = BuildStartInfo(rule.Command, cwd!);
        try
        {
            var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process.Start returned null.");
            AttachDiagnostics(process, project.Id, url.Id);
            _running[key] = process;
            _logger.LogInformation(
                "project-url-started project={Id} url={UrlId} pid={Pid} command={Command} cwd={Cwd}",
                project.Id, url.Id, process.Id, rule.Command, cwd);
            return new ProjectUrlStartResult(true, url.Id, rule.Command, cwd, process.Id);
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            _logger.LogError(ex,
                "project-url-start-failed project={Id} url={UrlId} command={Command} cwd={Cwd}",
                project.Id, url.Id, rule.Command, cwd);
            throw new InvalidOperationException($"Failed to start dev server: {ex.Message}", ex);
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

    private void AttachDiagnostics(Process process, string projectId, string urlId)
    {
        process.EnableRaisingEvents = true;
        process.OutputDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogDebug("project-url-output project={ProjectId} url={UrlId} text={Text}",
                    projectId, urlId, e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                _logger.LogWarning("project-url-error project={ProjectId} url={UrlId} text={Text}",
                    projectId, urlId, e.Data);
        };
        process.Exited += (_, _) =>
            _logger.LogInformation("project-url-exited project={ProjectId} url={UrlId} pid={Pid} exitCode={ExitCode}",
                projectId, urlId, process.Id, process.ExitCode);

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        process.StandardInput.Close();
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
}

public sealed record ProjectUrlStartResult(
    bool Started,
    string UrlId,
    string Command,
    string Cwd,
    int ProcessId);
