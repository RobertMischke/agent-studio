using System.Runtime.CompilerServices;
using System.Text.Json;
using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Level-1 baseline of the CAR parity suite (AGT-2372,
/// <c>docs/operations/car-migration-plan.md</c> §3, T3).
///
/// <para>The fixtures under <c>testdata/cli-fixtures/streams/</c> are recorded
/// CLI transcripts, replayable through <c>fake-cli.mjs</c>. This test runs them
/// through <em>today's</em> classification path — <see cref="SentinelScanner"/>
/// for the lane verdict, <see cref="ProviderOutputEvidenceExtractor"/> plus
/// <see cref="ExecutionOutcomeAdapter"/> for the typed outcome — and pins the
/// answers. When AGT-2370/2371 move the process start onto CodingAgentRunner,
/// the CAR path has to reproduce exactly these values from exactly these bytes.
/// A pin that has to be edited is a behaviour change that needs a decision, not
/// a test fix.</para>
///
/// <para>Scope today: P1 (terminal <c>[[TASK_DONE]]</c>) and P5 (substantial
/// reply without a sentinel), each in all recorded forms. P5 is the sharp one:
/// the same scenario classifies differently in plaintext (today's remote alt
/// path) than in stream-json (what CAR will produce), and that divergence is
/// pinned here on purpose rather than discovered in production.</para>
/// </summary>
public sealed class ParityFixtureTests
{
    /// <summary>Mirrors <c>RemoteTaskRunner.Facts</c>: the worktree is a local, not yet published, output.</summary>
    private const string WorktreePath = "/srv/agent-host/worktrees/AGT-2372";

    public static TheoryData<string> P1Fixtures => new()
    {
        "p1-happy-done.claude.fixture",
        "p1-happy-done.codex.fixture",
        "p1-happy-done.plaintext.fixture",
    };

    public static TheoryData<string> P5StreamFixtures => new()
    {
        "p5-no-sentinel.claude.fixture",
        "p5-no-sentinel.codex.fixture",
    };

    [Theory]
    [MemberData(nameof(P1Fixtures))]
    public void P1_terminal_done_is_pinned_in_every_recorded_form(string fixtureName)
    {
        var fixture = CliFixture.Load(fixtureName);

        var sentinel = SentinelScanner.Scan(fixture.StdOut);
        Assert.Equal(RunOutcomeKind.Done, sentinel.Kind);
        Assert.Null(sentinel.Reason);
        Assert.Equal("4-auto-review", sentinel.TargetState);
        Assert.Equal("Remote run completed", sentinel.SummaryPrefix);

        var decision = ExecutionOutcomeAdapter.Classify(Facts(fixture));
        Assert.Equal(ExecutionOutcomeKind.SuccessfulCompletion, decision.Outcome);
        Assert.Equal(OutcomeConfidence.High, decision.Confidence);
        Assert.Null(decision.Ambiguity);
        Assert.False(decision.IsInfrastructureOutcome);
        // Local-only durable output: the result still has to be handed off.
        Assert.Equal(ExecutionRecoveryAction.RetryHandoff, decision.RecoveryAction);
        Assert.False(decision.InvokesCodingModel);
    }

    [Theory]
    [MemberData(nameof(P5StreamFixtures))]
    public void P5_without_sentinel_in_stream_json_is_completed_on_provider_evidence(string fixtureName)
    {
        var fixture = CliFixture.Load(fixtureName);

        // The lane verdict is honest: no sentinel, no terminal claim.
        var sentinel = SentinelScanner.Scan(fixture.StdOut);
        Assert.Equal(RunOutcomeKind.Unknown, sentinel.Kind);
        Assert.Null(sentinel.Reason);
        Assert.Equal("5-human-review", sentinel.TargetState);

        // The typed outcome is not: a provider completion plus a final assistant
        // reply is accepted as terminal evidence at medium confidence. This is
        // the behavioural jump the migration introduces on the remote path,
        // because remote runs plain text today (car-migration-plan §8).
        var decision = ExecutionOutcomeAdapter.Classify(Facts(fixture));
        Assert.Equal(ExecutionOutcomeKind.SuccessfulCompletion, decision.Outcome);
        Assert.Equal(OutcomeConfidence.Medium, decision.Confidence);
        Assert.NotNull(decision.Ambiguity);
        Assert.Contains("No terminal sentinel", decision.Ambiguity!);
        Assert.Equal(ExecutionRecoveryAction.RetryHandoff, decision.RecoveryAction);
    }

    [Fact]
    public void P5_without_sentinel_in_plaintext_stays_inconclusive()
    {
        var fixture = CliFixture.Load("p5-no-sentinel.plaintext.fixture");

        var sentinel = SentinelScanner.Scan(fixture.StdOut);
        Assert.Equal(RunOutcomeKind.Unknown, sentinel.Kind);
        Assert.Equal("5-human-review", sentinel.TargetState);

        var decision = ExecutionOutcomeAdapter.Classify(Facts(fixture));
        Assert.Equal(ExecutionOutcomeKind.ProtocolInconclusive, decision.Outcome);
        Assert.Equal(ExecutionRecoveryAction.AskForHumanInput, decision.RecoveryAction);
        Assert.False(decision.IsInfrastructureOutcome);
        Assert.False(decision.InvokesCodingModel);
    }

    [Fact]
    public void P5_same_scenario_classifies_differently_per_form_and_that_is_the_migration_risk()
    {
        var plaintext = ExecutionOutcomeAdapter.Classify(Facts(CliFixture.Load("p5-no-sentinel.plaintext.fixture")));
        var streamed = ExecutionOutcomeAdapter.Classify(Facts(CliFixture.Load("p5-no-sentinel.claude.fixture")));

        Assert.NotEqual(plaintext.Outcome, streamed.Outcome);
        Assert.Equal(ExecutionOutcomeKind.ProtocolInconclusive, plaintext.Outcome);
        Assert.Equal(ExecutionOutcomeKind.SuccessfulCompletion, streamed.Outcome);
    }

    [Fact]
    public void P22_rate_limit_frames_are_informational_and_never_move_the_verdict()
    {
        foreach (var name in new[] { "p22-rate-limit-camel.claude.fixture", "p22-rate-limit-snake.claude.fixture" })
        {
            var fixture = CliFixture.Load(name);
            Assert.Contains("\"type\":\"rate_limit_event\"", fixture.StdOut);
            Assert.Equal(RunOutcomeKind.Done, SentinelScanner.Scan(fixture.StdOut).Kind);
            Assert.Equal(
                ExecutionOutcomeKind.SuccessfulCompletion,
                ExecutionOutcomeAdapter.Classify(Facts(fixture)).Outcome);
        }
    }

    [Fact]
    public void Every_recorded_fixture_is_well_formed()
    {
        var problems = new List<string>();
        foreach (var file in CliFixture.All())
        {
            var name = Path.GetFileName(file);
            CliFixture fixture;
            try
            {
                fixture = CliFixture.Load(name);
            }
            catch (Exception ex)
            {
                problems.Add($"{name}: {ex.Message}");
                continue;
            }

            var parts = name.Split('.');
            if (parts.Length != 3 || parts[2] != "fixture")
            {
                problems.Add($"{name}: expected <scenario-slug>.<claude|codex|plaintext>.fixture");
                continue;
            }

            var expectedFormToken = fixture.Form == "plaintext" ? "plaintext" : fixture.Cli;
            if (parts[1] != expectedFormToken)
                problems.Add($"{name}: file name says '{parts[1]}', metadata says '{expectedFormToken}'");
            if (!parts[0].StartsWith(fixture.Scenario.ToLowerInvariant(), StringComparison.Ordinal))
                problems.Add($"{name}: file name does not start with scenario '{fixture.Scenario}'");
            if (string.IsNullOrWhiteSpace(fixture.Title))
                problems.Add($"{name}: metadata has no title");
            if (string.IsNullOrWhiteSpace(fixture.StdOut))
                problems.Add($"{name}: replays nothing on stdout");

            if (fixture.Form != "stream-json") continue;
            foreach (var line in fixture.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                try { using var _ = JsonDocument.Parse(line); }
                catch (JsonException ex) { problems.Add($"{name}: stdout line is not a frame: {ex.Message}"); }
            }
        }

        Assert.True(problems.Count == 0, string.Join("\n  ", problems));
    }

    /// <summary>
    /// Reproduces the fact set <c>RemoteTaskRunner.ClassifyProcessResult</c>
    /// assembles today, minus the lease/workspace identity: provider evidence
    /// from stdout, the session marked resumable only when a resume command
    /// exists (it does not, RUNNER_CLI_RESUME_ARGS is unset in production), and
    /// the worktree as a local-only durable output.
    /// </summary>
    private static ExecutionRawFacts Facts(CliFixture fixture)
    {
        var provider = ProviderOutputEvidenceExtractor.Extract(fixture.StdOut);
        var sessionState = string.IsNullOrWhiteSpace(provider.SessionId)
            ? ExecutionSessionState.Unsupported
            : ExecutionSessionState.Active;

        return new ExecutionRawFacts(
            $"attempt-parity-{fixture.Scenario.ToLowerInvariant()}",
            ExecutionAttemptKind.Coding,
            provider.TerminalEvent,
            provider.FinalAssistantOutput,
            fixture.StdOut,
            fixture.StdErr,
            fixture.ExitCode,
            SignalFromExitCode(fixture.ExitCode),
            SessionState: sessionState,
            SessionId: provider.SessionId,
            DurableOutputState: DurableOutputState.LocalOnly,
            DurableOutputReference: WorktreePath);
    }

    /// <summary>Mirrors <c>RemoteTaskRunner.SignalFromExitCode</c>.</summary>
    private static int? SignalFromExitCode(int exitCode)
        => !OperatingSystem.IsWindows() && exitCode is >= 129 and <= 255 ? exitCode - 128 : null;

    /// <summary>
    /// A recorded CLI transcript. The grammar is documented in
    /// <c>testdata/cli-fixtures/README.md</c> and is shared with
    /// <c>fake-cli.mjs</c>, so the same file drives an in-process classification
    /// test and an out-of-process replay.
    /// </summary>
    private sealed record CliFixture(
        string Name,
        string Scenario,
        string Title,
        string Cli,
        string Form,
        int ExitCode,
        string StdOut,
        string StdErr)
    {
        private static string FixtureDirectory()
            => Path.Combine(RepoRoot(), "testdata", "cli-fixtures", "streams");

        public static IEnumerable<string> All()
            => Directory
                .EnumerateFiles(FixtureDirectory(), "*.fixture", SearchOption.TopDirectoryOnly)
                .OrderBy(file => file, StringComparer.Ordinal);

        public static CliFixture Load(string name)
        {
            var path = Path.Combine(FixtureDirectory(), name);
            if (!File.Exists(path)) throw new FileNotFoundException($"Fixture '{name}' not found.", path);

            JsonElement meta = default;
            var seenMeta = false;
            var stdout = new List<string>();
            var stderr = new List<string>();

            foreach (var line in File.ReadAllText(path).Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length == 0) continue;
                if (line.StartsWith("#!", StringComparison.Ordinal))
                {
                    if (seenMeta) throw new InvalidDataException($"Fixture '{name}' has more than one metadata line.");
                    meta = JsonDocument.Parse(line[2..].Trim()).RootElement.Clone();
                    seenMeta = true;
                    continue;
                }
                if (line.StartsWith('#')) continue;
                if (!seenMeta) throw new InvalidDataException($"Fixture '{name}' does not open with a '#!' metadata line.");
                if (line.StartsWith("@delay ", StringComparison.Ordinal)) continue;
                if (line.StartsWith("!stderr ", StringComparison.Ordinal)) { stderr.Add(line[8..]); continue; }
                stdout.Add(line);
            }

            if (!seenMeta) throw new InvalidDataException($"Fixture '{name}' has no '#!' metadata line.");

            return new CliFixture(
                name,
                Text(meta, "scenario"),
                Text(meta, "title"),
                Text(meta, "cli"),
                Text(meta, "form"),
                meta.TryGetProperty("exitCode", out var exit) ? exit.GetInt32() : 0,
                string.Join('\n', stdout),
                string.Join('\n', stderr));
        }

        private static string Text(JsonElement meta, string property)
            => meta.ValueKind == JsonValueKind.Object
               && meta.TryGetProperty(property, out var value)
               && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }

    private static string RepoRoot([CallerFilePath] string sourceFile = "")
    {
        foreach (var start in new[] { Path.GetDirectoryName(sourceFile), AppContext.BaseDirectory })
        {
            var current = start;
            while (!string.IsNullOrEmpty(current))
            {
                if (File.Exists(Path.Combine(current, "agent-taskboard.sln"))) return current;
                current = Path.GetDirectoryName(current);
            }
        }

        throw new InvalidOperationException(
            "agent-taskboard.sln not found above the parity test source file or the test base directory.");
    }
}
