namespace AgentRunner;

/// <summary>
/// Spawns the coding-agent CLI (claude / codex) headlessly on the runner host.
/// The prompt is fed on stdin and the configured args select print/headless mode;
/// the CLI runs inside the checked-out repo so it edits a real working tree. The
/// results directory is exposed as <c>JOB_RESULTS_DIR</c> so agents that follow
/// the protocol write evidence where the runner can find and upload it.
///
/// <para>
/// The exact CLI flags are intentionally configurable (RUNNER_CLI_BIN /
/// RUNNER_CLI_ARGS): headless auth and print-mode flags differ per CLI and per
/// version, and this MVP's job is to prove the transport contract, not to
/// re-implement the server's full CLI invocation. See the runbook for the
/// recommended per-CLI defaults.
/// </para>
/// </summary>
public sealed class AgentCliProcess
{
    private readonly RunnerOptions _options;
    private readonly string _resultsDir;
    private readonly Action<string> _log;

    public AgentCliProcess(RunnerOptions options, string resultsDir, Action<string> log)
    {
        _options = options;
        _resultsDir = resultsDir;
        _log = log;
    }

    public async Task<ProcessResult> RunAsync(
        string repoPath,
        string prompt,
        Action<string> onStdOut,
        Action<string> onStdErr,
        CancellationToken ct,
        string? argsOverride = null)
    {
        var args = SplitArgs(argsOverride ?? _options.CliArgs);
        _log($"exec: {_options.CliBin} {string.Join(' ', args)} (cwd {repoPath}, prompt {prompt.Length} chars on stdin)");

        return await ProcessRunner.RunAsync(
            _options.CliBin,
            args,
            workingDirectory: repoPath,
            stdin: prompt,
            onStdOut: line => { Console.Out.WriteLine(line); onStdOut(line); },
            onStdErr: line => { Console.Error.WriteLine(line); onStdErr(line); },
            environment: new Dictionary<string, string?> { ["JOB_RESULTS_DIR"] = _resultsDir },
            ct: ct);
    }

    // Whitespace split with minimal double-quote support. Good enough for the
    // handful of flags a headless CLI takes; complex quoting belongs in a wrapper
    // script named by RUNNER_CLI_BIN, not in this parser.
    public static List<string> SplitArgs(string raw)
    {
        var args = new List<string>();
        if (string.IsNullOrWhiteSpace(raw)) return args;

        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in raw)
        {
            if (c == '"') { inQuotes = !inQuotes; continue; }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0) { args.Add(current.ToString()); current.Clear(); }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) args.Add(current.ToString());
        return args;
    }
}
