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

    /// <summary>
    /// One resolved CLI invocation: which binary runs, with which arguments, and
    /// which parts of the decision came from the card's spec rather than from the
    /// host configuration. <see cref="Source"/> is what the spawn log states, so
    /// an operator can tell from one line whether the card, the environment, or a
    /// provider fallback decided this run.
    /// </summary>
    public sealed record CliInvocation(
        string FileName,
        IReadOnlyList<string> Arguments,
        string CliType,
        string? Model,
        string? ThinkingLevel,
        bool SpecApplied,
        string Source,
        string? Note = null);

    /// <summary>
    /// T0b — build the CLI invocation for one remote run from the card's
    /// execution spec, falling back to <c>RUNNER_CLI_BIN</c> / <c>RUNNER_CLI_ARGS</c>
    /// for everything the spec leaves open. This is the minimal remote counterpart
    /// of the local argv construction in
    /// <c>backend/Features/Cli/Execution/BuiltInCliBehaviors.cs</c>: only the
    /// model and reasoning selectors are ported, because those are the two the
    /// claim can state without changing how the remote path talks to the CLI.
    ///
    /// <para>
    /// Deliberately NOT ported here: permission flags, <c>stream-json</c> output,
    /// and the clean-context home. Each of those is a behaviour change of the
    /// remote path in its own right and belongs to T1 (AGT-2370), where the CAR
    /// descriptor builds the whole argv; the prompt keeps travelling on stdin as
    /// it does today.
    /// </para>
    ///
    /// <para>
    /// The host knows exactly two binaries — <c>RUNNER_CLI_BIN</c> (claude in
    /// practice) and <c>RUNNER_CODEX_CLI_BIN</c>. If a card asks for a CLI this
    /// host has no binary for, the configured binary wins and the invocation says
    /// so in <see cref="CliInvocation.Note"/> rather than spawning something that
    /// cannot exist.
    /// </para>
    /// </summary>
    public static CliInvocation Resolve(
        RunnerOptions options,
        RunSpecDto? runSpec,
        IReadOnlyList<string>? argsOverride = null)
    {
        var configuredType = LooksLikeCodex(options.CliBin) ? CodexCli : ClaudeCli;
        var requestedType = NormalizeCliType(runSpec?.CliType);

        var cliType = configuredType;
        var fileName = options.CliBin;
        string? note = null;
        if (requestedType is not null && !string.Equals(requestedType, configuredType, StringComparison.Ordinal))
        {
            if (requestedType == CodexCli && !string.IsNullOrWhiteSpace(options.CodexCliBin))
            {
                cliType = CodexCli;
                fileName = options.CodexCliBin;
            }
            else
            {
                note = $"card asked for cli={requestedType} but this host only has '{options.CliBin}'";
            }
        }

        // A model or reasoning selector is meaningful only to the CLI provider
        // named by the card. If that CLI is unavailable and the host falls back
        // to its configured CLI, let RUNNER_CLI_ARGS select that CLI's own
        // default instead of cross-applying a foreign provider's pins.
        var modelPinsDropped = requestedType is not null
                               && !string.Equals(requestedType, cliType, StringComparison.Ordinal);
        var args = argsOverride is not null
            ? new List<string>(argsOverride)
            : DefaultArgsFor(cliType, configuredType, options);

        // Model and reasoning selectors are appended, never inserted: the base
        // args are the operator's transport flags (-p / exec --experimental-json)
        // and both CLIs accept the selectors after them.
        var model = modelPinsDropped || string.IsNullOrWhiteSpace(runSpec?.Model)
            ? null
            : runSpec!.Model!.Trim();
        var thinkingLevel = modelPinsDropped || string.IsNullOrWhiteSpace(runSpec?.ThinkingLevel)
            ? null
            : runSpec!.ThinkingLevel!.Trim();
        if (model is not null)
        {
            args.Add(cliType == CodexCli ? "-m" : "--model");
            args.Add(model);
        }
        if (thinkingLevel is not null)
        {
            if (cliType == CodexCli)
            {
                args.Add("-c");
                args.Add($"model_reasoning_effort=\"{thinkingLevel}\"");
            }
            else
            {
                args.Add("--effort");
                args.Add(thinkingLevel);
            }
        }

        // Codex reads the prompt from stdin only when told to with the `-`
        // positional (the local path does the same). The runner always writes the
        // prompt to stdin, so a codex invocation without it would start with no
        // task at all.
        if (cliType == CodexCli && !args.Contains("-")) args.Add("-");

        var specApplied = model is not null || thinkingLevel is not null || requestedType is not null;
        return new CliInvocation(
            fileName,
            args,
            cliType,
            model,
            thinkingLevel,
            SpecApplied: specApplied,
            Source: modelPinsDropped
                ? "card-cli-fallback(model-pins-dropped)"
                : specApplied
                    ? "card"
                    : "runner-options",
            Note: note);
    }

    public const string ClaudeCli = "claude";
    public const string CodexCli = "codex";

    /// <summary>
    /// Canonical CLI id, or null when the spec names something this runner has no
    /// invocation form for. Null means "keep the configured binary" — unlike the
    /// backend's <c>CliTypes.Normalize</c>, an unknown value must not silently
    /// become claude out here, because that would override an operator's explicit
    /// <c>RUNNER_CLI_BIN</c> on the strength of a typo.
    /// </summary>
    public static string? NormalizeCliType(string? cliType) => cliType?.Trim().ToLowerInvariant() switch
    {
        ClaudeCli => ClaudeCli,
        CodexCli => CodexCli,
        _ => null,
    };

    private static bool LooksLikeCodex(string? bin)
        => !string.IsNullOrWhiteSpace(bin)
           && Path.GetFileNameWithoutExtension(bin).Contains(CodexCli, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Base arguments for the resolved CLI. <c>RUNNER_CLI_ARGS</c> stays the truth
    /// while the resolved CLI is the configured one; when the card routed the run
    /// to the other CLI, those args describe the wrong program and the minimal
    /// headless form for that CLI is used instead.
    /// </summary>
    private static List<string> DefaultArgsFor(string cliType, string configuredType, RunnerOptions options)
        => string.Equals(cliType, configuredType, StringComparison.Ordinal)
            ? SplitArgs(options.CliArgs)
            : cliType == CodexCli
                ? ["exec", "--experimental-json"]
                : ["-p"];

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
