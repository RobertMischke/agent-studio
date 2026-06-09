using System.Diagnostics;
using System.Text;

namespace OrchestratorApi.Services.Pipeline;

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
        PostStepMode mode,
        TimeSpan timeout,
        CancellationToken ct);
}

/// <summary>
/// Deterministic build post-step. It compiles the repository itself, so the
/// orchestrator never accepts a self-reported Success while the committed code
/// is broken.
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
        PostStepMode mode,
        TimeSpan timeout,
        CancellationToken ct)
    {
        if (mode == PostStepMode.Off)
        {
            return new BuildTestGateResult(
                BuildTestGateVerdict.Skipped, null, 0, "", "mode=off",
                RanBackendBuild: false, RanFrontendBuild: false);
        }

        if (!Directory.Exists(repositoryPath))
        {
            return new BuildTestGateResult(
                BuildTestGateVerdict.Skipped, null, 0, "", $"repository not found: {repositoryPath}",
                RanBackendBuild: false, RanFrontendBuild: false);
        }

        if (changedFiles is { Count: > 0 } && !HasCodeDiff(changedFiles))
        {
            return new BuildTestGateResult(
                BuildTestGateVerdict.Skipped, null, 0, "", "no code diff",
                RanBackendBuild: false, RanFrontendBuild: false);
        }

        var sw = Stopwatch.StartNew();
        var output = new BoundedOutput(MaxOutputLines);
        var backendExitCode = await RunProcessAsync(
            repositoryPath,
            "dotnet",
            ["build", Path.Combine("backend", "OrchestratorApi.csproj")],
            timeout,
            output,
            ct);
        if (backendExitCode != 0)
        {
            sw.Stop();
            var verdict = mode == PostStepMode.Fail ? BuildTestGateVerdict.Fail : BuildTestGateVerdict.Warn;
            return new BuildTestGateResult(verdict, backendExitCode, sw.ElapsedMilliseconds,
                output.Text, $"dotnet build exit {backendExitCode?.ToString() ?? "n/a"}", true, false);
        }

        var shouldRunFrontend = changedFiles == null || changedFiles.Any(IsFrontendPath);
        if (shouldRunFrontend && Directory.Exists(Path.Combine(repositoryPath, "frontend")))
        {
            var frontendRemaining = Remaining(timeout, sw.Elapsed);
            var frontendExitCode = await RunProcessAsync(
                Path.Combine(repositoryPath, "frontend"),
                OperatingSystem.IsWindows() ? "cmd.exe" : "/bin/sh",
                OperatingSystem.IsWindows()
                    ? ["/c", "npm run build"]
                    : ["-c", "npm run build"],
                frontendRemaining,
                output,
                ct);
            if (frontendExitCode != 0)
            {
                sw.Stop();
                var verdict = mode == PostStepMode.Fail ? BuildTestGateVerdict.Fail : BuildTestGateVerdict.Warn;
                return new BuildTestGateResult(verdict, frontendExitCode, sw.ElapsedMilliseconds,
                    output.Text, $"frontend build exit {frontendExitCode?.ToString() ?? "n/a"}", true, true);
            }
        }

        sw.Stop();
        return new BuildTestGateResult(BuildTestGateVerdict.Ok, 0, sw.ElapsedMilliseconds,
            output.Text, "build gate passed", true, shouldRunFrontend);
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

    private static bool IsFrontendPath(string path)
        => path.Replace('\\', '/').StartsWith("frontend/", StringComparison.OrdinalIgnoreCase);

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
            try { p.Kill(entireProcessTree: true); } catch { /* best effort */ }
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
