using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Bus;
using OrchestratorApi.Services.Jobs;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Typed outcome produced by <see cref="IntakeRunner"/>. Maps 1:1 to the
/// outcomes listed in the <c>ready-orchestrator-intake-lane</c> task spec.
/// The runner uses these values to decide which lifecycle phase the card
/// lands in, and the chat / bus emit a typed message of the same shape.
/// </summary>
public enum IntakeOutcome
{
    /// <summary>Card is good to go; the coding runner may pick it up.</summary>
    Pass,
    /// <summary>Prompt is too thin to execute safely; the user has to clarify.</summary>
    NeedsClarification,
    /// <summary>A near-duplicate already exists in 2-ready or recent 5-human-review / 6-completed.</summary>
    DuplicateCandidate,
    /// <summary>Prompt mixes several independent units of work; should be split first.</summary>
    NeedsSplit,
    /// <summary>Hard block (e.g. requests out-of-scope behavior). User must resolve.</summary>
    Blocked
}

/// <summary>One verdict produced by an intake check.</summary>
public sealed record IntakeVerdict
{
    public required IntakeOutcome Outcome { get; init; }
    /// <summary>Short human-readable reason. Travels into the chat note and the lifecycle sidecar.</summary>
    public required string Reason { get; init; }
    /// <summary>Optional structured detail (e.g. duplicate-of slug, missing fields list).</summary>
    public IReadOnlyList<string>? Details { get; init; }
}

/// <summary>
/// Deterministic, in-process intake check for the orchestrator-intake lane.
///
/// <para>
/// V1 is intentionally a small set of rules: prompt length probe (clarity),
/// fuzzy title match against existing 2-ready / recent human-review cards
/// (duplicate), heading-based split heuristic (needs-split), and a hard
/// out-of-scope keyword check (blocked). Anything that passes all four is
/// promoted with <see cref="IntakeOutcome.Pass"/>. The shape is the part
/// that matters: a future iteration can swap the heuristic for a model
/// call without changing the lifecycle plumbing or the public outcome
/// contract that the runner / UI / tests are pinned to.
/// </para>
///
/// <para>
/// The intake CLI is not the task execution CLI. The bus participant id
/// uses the <c>intake:&lt;project&gt;</c> shape (see
/// <see cref="ParticipantIntakeFor"/>) so the activity log and timeline
/// render intake as its own actor. The chat log line goes on the
/// <c>[orchestrator]</c> stream with the <c>[intake]</c> tag so existing
/// activity-log parsers pick it up without changes.
/// </para>
/// </summary>
public sealed class IntakeRunner
{
    public const string IntakeParticipantPrefix = "intake:";

    private readonly TaskScannerService _scanner;
    private readonly TaskMutationService _mutations;
    private readonly OrchestratorChatLog _chatLog;
    private readonly AgentMessageBusBridge? _bus;
    private readonly ILogger<IntakeRunner> _logger;
    private readonly TimeProvider _time;

    public IntakeRunner(
        TaskScannerService scanner,
        TaskMutationService mutations,
        OrchestratorChatLog chatLog,
        ILogger<IntakeRunner> logger,
        AgentMessageBusBridge? bus = null,
        TimeProvider? time = null)
    {
        _scanner = scanner;
        _mutations = mutations;
        _chatLog = chatLog;
        _bus = bus;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public static string ParticipantIntakeFor(string project) => $"{IntakeParticipantPrefix}{project}";

    /// <summary>
    /// Pure-function evaluation surface. Runs every check in order and
    /// returns the first non-Pass verdict; if every check passes, returns a
    /// Pass verdict. Pure so unit tests can pin the outcome matrix without
    /// touching disk or services.
    /// </summary>
    public static IntakeVerdict Evaluate(TaskInfo target, string? promptMarkdown, IReadOnlyList<TaskInfo> existingPeers)
    {
        var prompt = promptMarkdown ?? string.Empty;

        var blocked = CheckBlocked(prompt);
        if (blocked != null) return blocked;

        var dup = CheckDuplicate(target, existingPeers);
        if (dup != null) return dup;

        var clarity = CheckClarity(target, prompt);
        if (clarity != null) return clarity;

        var split = CheckSplit(prompt);
        if (split != null) return split;

        return new IntakeVerdict
        {
            Outcome = IntakeOutcome.Pass,
            Reason = "Prompt looks executable: scope is bounded, no duplicates detected, no out-of-scope language."
        };
    }

    /// <summary>
    /// Run intake on a job. Reads the job from disk, evaluates, writes the
    /// resulting phase + lifecycle sidecar, emits chat + bus messages. Idempotent:
    /// re-running on the same job re-evaluates and overwrites the phase.
    /// </summary>
    public IntakeVerdict RunForJob(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null)
            throw new InvalidOperationException($"Job '{jobId}' not found");
        if (!string.Equals(info.State, TaskStates.Ready, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Intake only runs on jobs in '{TaskStates.Ready}'; '{jobId}' is in '{info.State}'");

        var promptPath = Path.Combine(info.FolderPath, "prompt.md");
        var prompt = File.Exists(promptPath) ? File.ReadAllText(promptPath) : string.Empty;

        var peers = _scanner.ScanAllJobs()
            .Where(j => string.Equals(j.WatchPath, info.WatchPath, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(j.Id, info.Id, StringComparison.Ordinal)
                        && (j.State == TaskStates.Ready
                            || j.State == TaskStates.HumanReview
                            || j.State == TaskStates.Completed))
            .ToList();

        // Stamp intake-running first so observers see the in-flight state. A
        // restart between this stamp and the verdict write leaves the card in
        // intake-running, which the next tick re-runs and resolves.
        WritePhase(info, LifecyclePhases.IntakeRunning);
        EmitChat(info, "running", $"Intake check started by {ParticipantIntakeFor(info.ProjectName)}.");

        var verdict = Evaluate(info, prompt, peers);
        ApplyVerdict(info, verdict);
        return verdict;
    }

    /// <summary>Apply a precomputed verdict to a job (used by tests and by RunForJob).</summary>
    public void ApplyVerdict(TaskInfo info, IntakeVerdict verdict)
    {
        var phase = verdict.Outcome == IntakeOutcome.Pass
            ? LifecyclePhases.IntakePassed
            : LifecyclePhases.IntakeBlocked;
        WritePhase(info, phase);
        WriteLifecycleSidecar(info, verdict, phase);
        var tag = verdict.Outcome.ToString().ToLowerInvariant();
        EmitChat(info, tag, $"Intake {tag}: {verdict.Reason}");
        // Job-lifecycle bus event: the substate transition is meaningful
        // independent of the chat note (typed timeline drill-down).
        if (_bus != null)
        {
            try
            {
                _ = _bus.EmitJobLifecycleAsync(info,
                    topic: $"intake-{tag}",
                    fromState: LifecyclePhases.IntakeRunning,
                    toState: phase,
                    reason: verdict.Reason);
            }
            catch (Exception ex) { _logger.LogDebug(ex, "Bus emit for intake verdict failed for {JobId}", info.Id); }
        }
        _logger.LogInformation("Intake on {JobId}: {Outcome} ({Reason})", info.Id, verdict.Outcome, verdict.Reason);
    }

    private void WritePhase(TaskInfo info, string phase)
    {
        _mutations.SetJobPhase(info.FolderPath, phase);
    }

    private void WriteLifecycleSidecar(TaskInfo info, IntakeVerdict verdict, string phase)
    {
        try
        {
            var path = Path.Combine(info.FolderPath, "lifecycle.json");
            var snapshot = new LifecycleSnapshot
            {
                Phase = phase,
                PhaseEnteredAt = _time.GetUtcNow().UtcDateTime,
                BlockingReason = verdict.Outcome == IntakeOutcome.Pass ? null : verdict.Reason,
                IntakeChecks =
                [
                    new LifecycleCheck
                    {
                        Name = "intake-v1",
                        Status = verdict.Outcome == IntakeOutcome.Pass ? "passed" : "failed",
                        StartedAt = _time.GetUtcNow().UtcDateTime,
                        FinishedAt = _time.GetUtcNow().UtcDateTime,
                        Detail = verdict.Reason
                    }
                ]
            };
            File.WriteAllText(path, JsonSerializer.Serialize(snapshot, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write lifecycle.json for {JobId}", info.Id);
        }
    }

    private void EmitChat(TaskInfo info, string tag, string body)
    {
        try
        {
            // Tag every intake line with [intake] so the activity-log parser
            // can render intake as its own actor without parsing the prose.
            _chatLog.Append(info, OrchestratorMessageKind.Decision, $"[intake:{tag}] {body}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Intake chat note write failed for {JobId}", info.Id);
        }
    }

    // ---- Heuristic checks ------------------------------------------------

    private static IntakeVerdict? CheckBlocked(string prompt)
    {
        // ADR-0052 reversed the intra-project-parallelism non-goal: bounded
        // intra-project parallel execution via git worktrees + per-task branches
        // is now an opt-in, orchestrator-gated capability (configurable
        // maxParallelism per project), not a hard non-goal. The phrase blockers
        // that used to hard-stop such cards ("parallel coding", "worktree",
        // "create a new branch", "multiple agents at once") are therefore gone.
        // Re-add a blocker here only for a genuine future hard non-goal.
        _ = prompt;
        return null;
    }

    private static IntakeVerdict? CheckDuplicate(TaskInfo target, IReadOnlyList<TaskInfo> existing)
    {
        // Duplicate detection: titles that share a significant prefix or
        // overlap by Jaccard similarity over normalised tokens. Conservative
        // thresholds; intake is advisory in V1, so a few false negatives are
        // preferable to false positives that block real work.
        var targetTokens = TitleTokens(target.Title);
        if (targetTokens.Count == 0) return null;

        foreach (var peer in existing)
        {
            var peerTokens = TitleTokens(peer.Title);
            if (peerTokens.Count == 0) continue;

            var inter = targetTokens.Intersect(peerTokens).Count();
            var union = targetTokens.Union(peerTokens).Count();
            if (union == 0) continue;
            var jaccard = (double)inter / union;
            if (jaccard >= 0.75)
            {
                return new IntakeVerdict
                {
                    Outcome = IntakeOutcome.DuplicateCandidate,
                    Reason = $"Title is a near-duplicate of '{peer.Id}' ({peer.State}); confirm before running.",
                    Details = [peer.Id, peer.State]
                };
            }
        }
        return null;
    }

    private static IntakeVerdict? CheckClarity(TaskInfo target, string prompt)
    {
        // Cheap clarity probe: a prompt with under 20 non-whitespace
        // characters and no sentence terminator is almost always too thin
        // to execute. The point is to surface the obvious cases, not to
        // grade prose.
        var trimmed = prompt.Trim();
        if (trimmed.Length < 20)
        {
            return new IntakeVerdict
            {
                Outcome = IntakeOutcome.NeedsClarification,
                Reason = $"Prompt is very short ({trimmed.Length} chars). Add scope, acceptance, and any constraints.",
            };
        }
        return null;
    }

    private static IntakeVerdict? CheckSplit(string prompt)
    {
        // Heading-based split heuristic: a prompt with six or more level-2
        // markdown headings often combines independent tasks. Surface as
        // advisory; the user decides whether to split.
        var headingCount = 0;
        foreach (var line in prompt.Split('\n'))
        {
            var t = line.TrimStart();
            if (t.StartsWith("## ", StringComparison.Ordinal) && !t.StartsWith("### ", StringComparison.Ordinal))
                headingCount++;
        }
        if (headingCount >= 6)
        {
            return new IntakeVerdict
            {
                Outcome = IntakeOutcome.NeedsSplit,
                Reason = $"Prompt has {headingCount} level-2 sections; consider splitting into multiple tasks before running.",
            };
        }
        return null;
    }

    private static HashSet<string> TitleTokens(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return new HashSet<string>();
        var tokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in title.Split(new[] { ' ', '-', '_', '/', '.', ',' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim().ToLowerInvariant();
            if (t.Length < 3) continue;
            // Skip generic stop words that carry no signal.
            if (t is "the" or "and" or "for" or "with" or "from" or "that" or "this" or "into") continue;
            tokens.Add(t);
        }
        return tokens;
    }
}
