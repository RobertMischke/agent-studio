namespace AgentRunner;

/// <summary>One startup proof that the daemon can publish run salvage before it accepts work.</summary>
public static class GitPushProbe
{
    public static async Task<GitPushProbeResult> RunAsync(RunnerOptions options, Action<string> log, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.GitRemote))
            return new(false, "RUNNER_GIT_REMOTE is required for the startup push probe.");

        Directory.CreateDirectory(options.WorkDir);
        var probePath = Path.Combine(options.WorkDir, $".git-push-probe-{Environment.ProcessId}-{Guid.NewGuid():N}");
        try
        {
            log($"runner-git-push-probe-started fetchRemote={options.GitRemote} pushRemote={(options.GitPushRemote ?? "same-as-fetch")}");
            var clone = await ProcessRunner.RunAsync("git", ["clone", "--no-checkout", "--depth", "1", options.GitRemote, probePath], options.WorkDir, ct: ct);
            if (!clone.Success) return Failed("fetch", clone);
            if (!string.IsNullOrWhiteSpace(options.GitPushRemote))
            {
                var pushUrl = await ProcessRunner.RunAsync("git", ["remote", "set-url", "--push", "origin", options.GitPushRemote], probePath, ct: ct);
                if (!pushUrl.Success) return Failed("pushurl", pushUrl);
            }

            var probeRef = $"refs/heads/runner-capability-probe/{GitWorkspace.SafeSegment(options.RunnerId)}";
            var push = await ProcessRunner.RunAsync("git", ["push", "--dry-run", "origin", $"HEAD:{probeRef}"], probePath, ct: ct);
            return push.Success ? new(true, $"dry-run succeeded for {probeRef}") : Failed("push-dry-run", push);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new(false, $"probe exception: {OneLine(ex.Message)}");
        }
        finally
        {
            try { if (Directory.Exists(probePath)) Directory.Delete(probePath, recursive: true); }
            catch (Exception ex) { log($"runner-git-push-probe-cleanup-failed path={probePath} error={OneLine(ex.Message)}"); }
        }
    }

    private static GitPushProbeResult Failed(string stage, ProcessResult result)
        => new(false, $"{stage} failed ({result.ExitCode}): {OneLine(result.StdErr)}");

    private static string OneLine(string value) => value.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

public sealed record GitPushProbeResult(bool CanPush, string Detail);
