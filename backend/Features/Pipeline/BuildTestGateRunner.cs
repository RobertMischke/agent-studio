using System.Diagnostics;
using System.Text;

namespace AgentStudio.Pipeline;

public enum BuildTestGateVerdict
{
    Skipped,
    Ok,
    Warn,
    Fail,
}

public sealed record BuildTestGateResult(
    BuildTestGateVerdict Verdict,
    int? ExitCode,
    long DurationMs,
    string Output,
    string Reason,
    bool RanBackendBuild,
    bool RanFrontendBuild);

public interface IBuildTestGateRunner
{
    Task<BuildTestGateResult> RunAsync(
        string repositoryPath,
        IReadOnlyList<string>? changedFiles,
        BuildProfile? profile,
        PostStepMode mode,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>
/// Deterministic build/test post-step. It verifies the repository itself, so the
/// orchestrator never accepts a self-reported Success while the committed code is
/// broken.
///
/// <para>
/// The verify commands are <b>derived per project</b> by
/// <see cref="VerifyCommandPlanner"/> instead of hardcoded: an explicit build
/// profile is the override, otherwise the commands come from the repo layout
/// (bare <c>dotnet build</c>/<c>dotnet test</c> for a root <c>.sln</c>/<c>.csproj</c>,
/// <c>npm</c> scripts for a <c>package.json</c>). When nothing is derivable the
/// gate runs without a build check and says so in the verdict, rather than fail
/// against a path that does not exist (the TE-2 / AGT-2065 lesson: the old
/// hardcoded <c>backend/OrchestratorApi.csproj</c> broke on every project with a
/// different layout).
/// </para>
/// </summary>
public sealed class BuildTestGateRunner : IBuildTestGateRunner
{
    public const int MaxOutputLines = 80;

    private static readonly string[] CodeExtensions =
    [
        ".cs", ".csproj", ".sln", ".slnx", ".props", ".targets",
        ".ts", ".html", ".scss", ".css", ".json", ".mjs", ".js",
    ];

    private readonly ILogger<BuildTestGateRunner> _logger;

    public BuildTestGateRunner(ILogger<BuildTestGateRunner> logger)
    {
        _logger = logger;
    }

    public async Task<BuildTestGateResult> RunAsync(
        string repositoryPath,
        IReadOnlyList<string>? changedFiles,
        BuildProfile? profile,
        PostStepMode mode,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (mode == PostStepMode.Off)
            return Skipped("mode=off");

        if (!Directory.Exists(repositoryPath))
            return Skipped($"repository not found: {repositoryPath}");

        if (changedFiles is { Count: > 0 } && !HasCodeDiff(changedFiles))
            return Skipped("no code diff");

        // Derive the verify set per project rather than hardcoding a command.
        var plan = VerifyCommandPlanner.Plan(repositoryPath, profile);
        if (plan.IsEmpty)
        {
            // Honest fallback: no build profile, no root .sln/.csproj, no usable
            // package.json scripts. Run the gate without a build check and say so,
            // instead of failing against a path that does not exist.
            _logger.LogInformation(
                "BuildTestGateRunner: no verify commands derivable for {Repo}; gate runs without a build check",
                repositoryPath);
            return new BuildTestGateResult(
                BuildTestGateVerdict.Skipped, null, 0, "", "no verify commands derivable",
                RanBackendBuild: false, RanFrontendBuild: false);
        }

        // Node commands scoped to a subdir only run when a changed file touches
        // that subdir (preserves the old "frontend build only if frontend
        // changed" optimization); root-level commands run once past the diff gate.
        var toRun = plan.Commands.Where(c => ShouldRunForChange(c, changedFiles)).ToList();
        if (toRun.Count == 0)
            return Skipped($"no verify commands apply to the changed files ({plan.Source})");

        var sw = Stopwatch.StartNew();
        var output = new BoundedOutput(MaxOutputLines);
        output.AppendLine($"# verify plan: {plan.Source} ({toRun.Count} command(s))");

        var ranBackend = false;
        var ranFrontend = false;

        foreach (var cmd in toRun)
        {
            var workingDir = string.IsNullOrEmpty(cmd.WorkingSubdir)
                ? repositoryPath
                : Path.Combine(repositoryPath, cmd.WorkingSubdir);
            if (!Directory.Exists(workingDir))
            {
                output.AppendLine($"! skipped {Describe(cmd)} (missing directory)");
                continue;
            }

            // Node -> frontend flag; dotnet and verbatim build-profile commands
            // -> backend flag, so the log line still shows the gate did real work.
            if (cmd.Ecosystem == VerifyEcosystem.Node) ranFrontend = true;
            else ranBackend = true;

            var exit = await RunShellAsync(workingDir, cmd.Command, Remaining(timeout, sw.Elapsed), output, ct);
            if (exit != 0)
            {
                sw.Stop();
                var verdict = mode == PostStepMode.Fail ? BuildTestGateVerdict.Fail : BuildTestGateVerdict.Warn;
                return new BuildTestGateResult(verdict, exit, sw.ElapsedMilliseconds,
                    output.Text, $"{Describe(cmd)} exit {exit?.ToString() ?? "n/a"}", ranBackend, ranFrontend);
            }
        }

        sw.Stop();
        return new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, sw.ElapsedMilliseconds,
            output.Text, $"verify gate passed ({plan.Source})", ranBackend, ranFrontend);
    }

    private static BuildTestGateResult Skipped(string reason) =>
        new(BuildTestGateVerdict.Skipped, null, 0, "", reason,
            RanBackendBuild: false, RanFrontendBuild: false);

    /// <summary>
    /// Whether a derived command applies to this change set. A null change list is
    /// conservative (run everything). A node command scoped to a subdir runs only
    /// when a changed file lives under that subdir; every other command runs once
    /// the top-level code-diff gate has passed.
    /// </summary>
    private static bool ShouldRunForChange(VerifyCommand cmd, IReadOnlyList<string>? changedFiles)
    {
        if (changedFiles is null) return true;
        if (cmd.Ecosystem != VerifyEcosystem.Node || string.IsNullOrEmpty(cmd.WorkingSubdir))
            return true;

        var prefix = cmd.WorkingSubdir.Replace('\\', '/').TrimEnd('/') + "/";
        return changedFiles.Any(f =>
            f.Replace('\\', '/').StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string Describe(VerifyCommand cmd)
    {
        var where = string.IsNullOrEmpty(cmd.WorkingSubdir) ? "" : $" ({cmd.WorkingSubdir})";
        return $"`{cmd.Command}`{where}";
    }

    private Task<int?> RunShellAsync(
        string workingDirectory,
        string command,
        TimeSpan timeout,
        BoundedOutput output,
        CancellationToken ct)
    {
        var (fileName, args) = OperatingSystem.IsWindows()
            ? ("cmd.exe", (IReadOnlyList<string>)["/c", command])
            : ("/bin/sh", (IReadOnlyList<string>)["-c", command]);
        return RunProcessAsync(workingDirectory, fileName, args, timeout, output, ct);
    }

    internal static bool HasCodeDiff(IReadOnlyList<string> changedFiles)
        => changedFiles.Any(IsCodePath);

    private static bool IsCodePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith(".orchestrator/", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)) return false;
        if (normalized.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return false;
        var ext = Path.GetExtension(normalized);
        return CodeExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<int?> RunProcessAsync(
        string workingDirectory,
        string fileName,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        BoundedOutput output,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        output.AppendLine($"> {fileName} {string.Join(' ', args)}");

        Process? p;
        try { p = Process.Start(psi); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BuildTestGateRunner: Process.Start failed for {FileName}", fileName);
            output.AppendLine(ex.Message);
            return null;
        }
        if (p == null)
        {
            output.AppendLine("Process.Start returned null");
            return null;
        }

        p.OutputDataReceived += (_, e) => output.AppendLine(e.Data);
        p.ErrorDataReceived += (_, e) => output.AppendLine(e.Data);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token);
            return p.ExitCode;
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch (Exception __ex) { SilentCatch.Note(__ex, "BuildTestGateRunner: best effort"); /* best effort */ }
            output.AppendLine($"{fileName} timed out after {timeout.TotalSeconds:F0}s");
            return null;
        }
    }

    private static TimeSpan Remaining(TimeSpan timeout, TimeSpan elapsed)
    {
        var remaining = timeout - elapsed;
        return remaining > TimeSpan.FromSeconds(10) ? remaining : TimeSpan.FromSeconds(10);
    }

    private sealed class BoundedOutput
    {
        private readonly int _maxLines;
        private readonly StringBuilder _builder = new();
        private readonly object _lock = new();
        private int _lineCount;

        public BoundedOutput(int maxLines)
        {
            _maxLines = maxLines;
        }

        public string Text
        {
            get
            {
                lock (_lock) return _builder.ToString().TrimEnd();
            }
        }

        public void AppendLine(string? line)
        {
            if (line == null) return;
            lock (_lock)
            {
                if (_lineCount >= _maxLines) return;
                _builder.AppendLine(line);
                _lineCount++;
            }
        }
    }
}
