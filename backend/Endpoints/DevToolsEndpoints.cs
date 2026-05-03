using System.Diagnostics;
using System.Text;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Endpoints;

/// <summary>
/// Tool-development-only routes guarded by per-checkout config flags
/// in <c>appsettings.Local.json</c>:
///
/// <list type="bullet">
///   <item><c>DevTools:UpdateStableEnabled</c> -> SSE stream of
///   <c>update-stable.sh</c>. Only meaningful in the dev checkout, since
///   the script restarts stable.</item>
///   <item><c>DevTools:DeleteE2EJobsEnabled</c> -> list and bulk-delete
///   jobs whose id or title contains "E2E" (case-insensitive), across
///   every configured watch path.</item>
/// </list>
///
/// Both flags default to false. The frontend reads them via
/// <c>/api/environment</c> and only renders the corresponding header
/// buttons when the flags are set.
/// </summary>
public static class DevToolsEndpoints
{
    public static void MapDevToolsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/devtools");

        group.MapGet("/update-stable/stream", async (
            HttpContext http,
            IConfiguration config,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            if (!config.GetValue<bool>("DevTools:UpdateStableEnabled"))
            {
                http.Response.StatusCode = StatusCodes.Status403Forbidden;
                await http.Response.WriteAsync("DevTools:UpdateStableEnabled is false", ct);
                return;
            }

            var logger = loggerFactory.CreateLogger("DevTools.UpdateStable");
            var scriptPath = ResolveUpdateStableScript(config);
            if (scriptPath is null)
            {
                http.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await http.Response.WriteAsync("update-stable.sh not found. Set DevTools:UpdateStableScriptPath or place update-stable.sh in the devspace root.", ct);
                return;
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no";

            await StreamProcessAsync(http, scriptPath, logger, ct);
        });

        group.MapGet("/e2e-jobs", (IConfiguration config, JobScannerService scanner) =>
        {
            if (!config.GetValue<bool>("DevTools:DeleteE2EJobsEnabled"))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var matches = scanner.ScanAllJobs()
                .Where(IsE2EJob)
                .Select(j => new
                {
                    jobKey = j.JobKey,
                    id = j.Id,
                    title = j.Title,
                    state = j.State,
                    projectName = j.ProjectName,
                    watchPath = j.WatchPath
                })
                .OrderBy(j => j.projectName)
                .ThenBy(j => j.id)
                .ToList();

            return Results.Ok(matches);
        });

        group.MapPost("/e2e-jobs/delete", (
            IConfiguration config,
            JobScannerService scanner,
            JobStateMachine states,
            DeleteE2EJobsRequest req) =>
        {
            if (!config.GetValue<bool>("DevTools:DeleteE2EJobsEnabled"))
                return Results.StatusCode(StatusCodes.Status403Forbidden);

            var requested = req.JobKeys ?? new List<string>();
            var keySet = new HashSet<string>(requested, StringComparer.Ordinal);

            var candidates = scanner.ScanAllJobs()
                .Where(IsE2EJob)
                .Where(j => keySet.Count == 0 || keySet.Contains(j.JobKey))
                .ToList();

            var deleted = new List<string>();
            var failed = new List<string>();
            foreach (var job in candidates)
            {
                if (states.DeleteJob(job.Id, job.WatchPath))
                    deleted.Add(job.JobKey);
                else
                    failed.Add(job.JobKey);
            }

            return Results.Ok(new
            {
                deletedCount = deleted.Count,
                failedCount = failed.Count,
                deleted,
                failed
            });
        });
    }

    private static bool IsE2EJob(JobInfo job)
    {
        return ContainsE2E(job.Id) || ContainsE2E(job.Title);
    }

    private static bool ContainsE2E(string? value)
    {
        return !string.IsNullOrEmpty(value)
            && value.Contains("E2E", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveUpdateStableScript(IConfiguration config)
    {
        var configured = config["DevTools:UpdateStableScriptPath"];
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        // Walk up from the backend cwd looking for update-stable.sh; the
        // devspace layout puts it one or two directories above (e.g. the
        // backend runs from agent-taskboard-dev/ and the script lives in
        // agent-taskboard-devspace/).
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        for (var i = 0; i < 5 && dir != null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "update-stable.sh");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static async Task StreamProcessAsync(HttpContext http, string scriptPath, ILogger logger, CancellationToken ct)
    {
        var bash = ResolveBashExecutable();
        var workingDir = Path.GetDirectoryName(scriptPath) ?? Directory.GetCurrentDirectory();

        var psi = new ProcessStartInfo
        {
            FileName = bash,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        psi.ArgumentList.Add(ToBashPath(scriptPath));

        await WriteSseAsync(http, "log", $"$ {bash} {string.Join(' ', psi.ArgumentList)}", ct);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var pumpTasks = new List<Task>();

        try
        {
            if (!proc.Start())
            {
                await WriteSseAsync(http, "error", "Failed to start update-stable.sh", ct);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to launch update-stable.sh");
            await WriteSseAsync(http, "error", $"Failed to launch: {ex.Message}", ct);
            return;
        }

        pumpTasks.Add(PumpStreamAsync(proc.StandardOutput, "stdout", http, ct));
        pumpTasks.Add(PumpStreamAsync(proc.StandardError, "stderr", http, ct));

        try
        {
            await proc.WaitForExitAsync(ct);
            await Task.WhenAll(pumpTasks);
            await WriteSseAsync(http, "done", $"exit code {proc.ExitCode}", ct);
        }
        catch (OperationCanceledException)
        {
            // Do NOT kill the child. The most common cancellation cause here is
            // the script restarting THIS backend (when run from the same
            // checkout that the script targets). Killing the tree would kill
            // the in-flight stop/start chain and leave the instance down.
            // Best to let it run to completion; the user reloads the page.
        }
    }

    private static string ResolveBashExecutable()
    {
        // Order: env override -> Git Bash on Windows -> WSL bash -> plain bash.
        var fromEnv = Environment.GetEnvironmentVariable("DEVTOOLS_BASH");
        if (!string.IsNullOrWhiteSpace(fromEnv) && File.Exists(fromEnv)) return fromEnv;

        if (OperatingSystem.IsWindows())
        {
            string[] candidates =
            {
                @"C:\Program Files\Git\bin\bash.exe",
                @"C:\Program Files\Git\usr\bin\bash.exe",
                @"C:\Program Files (x86)\Git\bin\bash.exe"
            };
            foreach (var c in candidates) if (File.Exists(c)) return c;
        }
        return "bash";
    }

    private static string ToBashPath(string path)
    {
        // Git Bash accepts the native Windows path on the command line as long
        // as it's quoted; ProcessStartInfo.ArgumentList quotes for us.
        return path;
    }

    private static async Task PumpStreamAsync(StreamReader reader, string kind, HttpContext http, CancellationToken ct)
    {
        try
        {
            string? line;
            while ((line = await reader.ReadLineAsync(ct)) != null)
            {
                await WriteSseAsync(http, kind, line, ct);
            }
        }
        catch (OperationCanceledException) { }
    }

    private static async Task WriteSseAsync(HttpContext http, string evt, string data, CancellationToken ct)
    {
        // SSE wire format: each "data:" line is one piece of the event payload.
        // We send single-line payloads only, which keeps parsing on the client
        // trivial (one event = one log line).
        var sanitized = data.Replace("\r", "");
        var payload = $"event: {evt}\ndata: {sanitized}\n\n";
        var bytes = Encoding.UTF8.GetBytes(payload);
        await http.Response.Body.WriteAsync(bytes, ct);
        await http.Response.Body.FlushAsync(ct);
    }
}

public sealed class DeleteE2EJobsRequest
{
    public List<string>? JobKeys { get; set; }
}
