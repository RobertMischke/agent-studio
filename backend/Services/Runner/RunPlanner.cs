using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Why an upcoming CLI run was triggered. The planner uses this single
/// dimension to decide between the start-shaped and continue-shaped
/// branches; everything else (session state, job state, CLI compatibility)
/// is plain data. Keeping the trigger reason explicit in one enum is what
/// lets <see cref="RunPlanner.PlanRun"/> own the whole decision tree
/// instead of having the start and continue endpoints reinvent it
/// independently.
/// </summary>
public enum RunIntent
{
    /// <summary>User clicked Play / hit /jobs/{id}/start.</summary>
    ManualStart,
    /// <summary>Auto-pickup tick chose this job from the ready queue.</summary>
    AutoPickup,
    /// <summary>User typed in the chat / hit /jobs/{id}/continue with a follow-up.</summary>
    UserContinue
}

/// <summary>
/// Pure description of what a single CLI invocation should do. Produced by
/// <see cref="RunPlanner.PlanRun"/> from inputs (intent, job state, session
/// state, CLI capabilities) and consumed by <see cref="ProjectRunner"/>,
/// which then applies side-effects (state moves, scanner writes, log writes,
/// event append, CLI start). Splitting plan from apply keeps the decision
/// tree fully unit-testable without mocking the scanner or CLI.
/// </summary>
public sealed record RunPlan(
    string Prompt,
    string? SessionToResume,
    bool ResumeFlag,
    string EventKind,
    string? EventReason,
    string? EventInputSessionId,
    bool MoveJobToProgress,
    bool MarkSessionChainRecovery,
    bool WriteCutMarker,
    string? CutMarkerReason,
    string? PersistSessionName,
    bool ClearStaleSessionName);

/// <summary>
/// Pure decision library for runner invocations — no I/O, no field access,
/// no DI. Owns the full mapping from (intent × job state × session state ×
/// CLI compatibility) to a <see cref="RunPlan"/>, plus the prompt builders
/// and slug heuristics those decisions reference.
///
/// Why this is a separate static class: the previous design hid this logic
/// inside a stateful runner that also managed lifecycle and side-effects,
/// so the start and continue endpoints reinvented the decision tree
/// independently — and a recovery fix landed only on one side, producing
/// the "no session yet" 400 the user reported. Pulling the planner out as
/// a pure library makes that divergence structurally impossible: there is
/// only one function to change, and the matrix in <c>TaskRunnerPlanTests</c>
/// locks every cell of the table.
/// </summary>
public static class RunPlanner
{
    /// <summary>
    /// Maps a trigger + observed state to a fully-described <see cref="RunPlan"/>.
    /// Called from <see cref="ProjectRunner.RunCliAsync"/>; never throws, never
    /// returns null — the contract is "always produces a runnable plan", which
    /// is the property that closes the "no session yet" bug class.
    /// </summary>
    public static RunPlan PlanRun(
        RunIntent intent,
        string initialState,
        string? sessionName,
        string cliType,
        Func<string?, bool> isCompatibleSessionName,
        string jobId,
        string promptPath,
        string jobFolder,
        string? followupPrompt)
    {
        if (intent == RunIntent.UserContinue)
        {
            var hasSession = !string.IsNullOrWhiteSpace(sessionName);
            var compatible = hasSession && isCompatibleSessionName(sessionName);
            var placeholder = IsPlaceholderSessionSlug(sessionName);
            var canResume = hasSession && compatible && !placeholder;
            string? reason =
                !hasSession ? "no session recorded"
                : !compatible ? $"recorded id is not a valid {cliType} session"
                : placeholder ? "recorded id is a legacy placeholder slug"
                : null;
            var moveToProgress = initialState is JobStates.Review or JobStates.Completed or JobStates.Ready;

            if (canResume)
            {
                return new RunPlan(
                    Prompt: followupPrompt ?? string.Empty,
                    SessionToResume: sessionName,
                    ResumeFlag: true,
                    EventKind: "continue",
                    EventReason: null,
                    EventInputSessionId: sessionName,
                    MoveJobToProgress: moveToProgress,
                    MarkSessionChainRecovery: false,
                    WriteCutMarker: false,
                    CutMarkerReason: null,
                    PersistSessionName: null,
                    ClearStaleSessionName: false);
            }

            return new RunPlan(
                Prompt: BuildRecoveryContinuationPrompt(jobFolder, followupPrompt ?? string.Empty),
                SessionToResume: null,
                ResumeFlag: false,
                EventKind: "recovery",
                EventReason: reason,
                EventInputSessionId: null,
                MoveJobToProgress: moveToProgress,
                MarkSessionChainRecovery: true,
                WriteCutMarker: true,
                CutMarkerReason: reason ?? "session lost",
                PersistSessionName: null,
                ClearStaleSessionName: false);
        }

        // ManualStart / AutoPickup share the same plan shape — only the trigger
        // differs, and that is logged at the call site, not branched here.
        var moveStartToProgress = initialState == JobStates.Ready;
        var startSession = sessionName;
        var sessionDropped = false;
        var clearStale = false;
        var markRecovery = false;
        if (!string.IsNullOrWhiteSpace(startSession) && !isCompatibleSessionName(startSession))
        {
            var isLegacyPlaceholder = IsPlaceholderSessionSlug(startSession);
            startSession = null;
            if (isLegacyPlaceholder)
            {
                clearStale = true;
            }
            else
            {
                markRecovery = true;
                sessionDropped = true;
            }
        }
        var resume = !string.IsNullOrWhiteSpace(startSession);
        string? persistSessionName = null;
        if (!resume && cliType == CliTypes.Copilot)
        {
            // Copilot uses the persisted name as the resume handle — pre-generate
            // a slug now so the next run can find it. Other CLIs capture a real
            // UUID during streaming and leave SessionName null until then.
            startSession = BuildSessionName(jobId);
            persistSessionName = startSession;
        }

        var useResumePrompt = ShouldUseResumePrompt(initialState, resume, sessionDropped);
        var prompt = useResumePrompt
            ? BuildResumeContinuationPrompt(jobFolder)
            : BuildFreshStartPrompt(promptPath, jobFolder);

        string evtKind = sessionDropped ? "recovery" : (resume ? "continue" : "start");
        string? evtReason = sessionDropped ? "previous session was for another CLI — files reconstructed" : null;

        return new RunPlan(
            Prompt: prompt,
            SessionToResume: startSession,
            ResumeFlag: resume,
            EventKind: evtKind,
            EventReason: evtReason,
            EventInputSessionId: resume ? startSession : null,
            MoveJobToProgress: moveStartToProgress,
            MarkSessionChainRecovery: markRecovery,
            WriteCutMarker: false,
            CutMarkerReason: null,
            PersistSessionName: persistSessionName,
            ClearStaleSessionName: clearStale);
    }

    /// <summary>
    /// Decides whether a start should inject the resume-continuation prompt
    /// instead of the fresh-start prompt.
    /// <list type="bullet">
    /// <item>Sending it when no real session exists and nothing was dropped just
    /// gets a "I don't see an interrupted task" reply and an exit — so a job that
    /// happens to be in 3-progress without a captured UUID is treated as a fresh
    /// start.</item>
    /// <item><c>sessionDropped</c> means the persisted session id was for another
    /// CLI; the agent that wrote the job folder did real work so reconstruction
    /// from files is worth attempting regardless of the current state.</item>
    /// </list>
    /// </summary>
    public static bool ShouldUseResumePrompt(string initialState, bool resume, bool sessionDropped)
    {
        if (sessionDropped) return true;
        if (initialState == JobStates.Progress && resume) return true;
        return false;
    }

    /// <summary>
    /// True for slugs we generated via <see cref="BuildSessionName"/> on an earlier
    /// run. These were never real sessions on the agent side — recognising them
    /// lets the cross-CLI guard drop them silently instead of treating the
    /// next start as a recovery from an interrupted run.
    /// </summary>
    public static bool IsPlaceholderSessionSlug(string? sessionName)
        => !string.IsNullOrWhiteSpace(sessionName)
           && PlaceholderSessionSlugRegex.IsMatch(sessionName!);

    private static readonly Regex PlaceholderSessionSlugRegex =
        new(@"^taskboard-[A-Za-z0-9_-]+-\d{12}$", RegexOptions.Compiled);

    /// <summary>
    /// Copilot uses the persisted name as a stable handle for <c>--resume</c>;
    /// keep it short, deterministic, and unique per start. Other CLIs capture
    /// a real UUID during streaming and never need this.
    /// </summary>
    public static string BuildSessionName(string jobId)
    {
        var slug = new string(jobId.Where(c => char.IsLetterOrDigit(c) || c == '-' || c == '_').ToArray());
        if (slug.Length > 40) slug = slug[..40];
        return $"taskboard-{slug}-{DateTime.UtcNow:yyyyMMddHHmm}";
    }

    /// <summary>
    /// Initial-run prompt: instructs the agent to read the task prompt and
    /// execute it. Used only for fresh starts (job came from 2-ready).
    /// </summary>
    public static string BuildFreshStartPrompt(string promptPath, string jobFolder)
        => $"Lies @\"{promptPath}\" und führe den Task aus. Der Job-Ordner ist \"{jobFolder}\".";

    /// <summary>
    /// Resume prompt: forces the agent to rebuild context from the job folder
    /// before doing anything else. Sent when the previous run was interrupted
    /// (job already in 3-progress) or when the persisted session id had to be
    /// dropped as incompatible — in both cases the model would otherwise treat
    /// the new turn as a fresh request and ask the user what to continue with.
    /// English on purpose: works across Claude / Codex / Gemini / Copilot
    /// regardless of the user's chat language.
    /// </summary>
    public static string BuildResumeContinuationPrompt(string jobFolder)
        => "Resume the interrupted task.\n\n"
         + "Task folder:\n"
         + $"{jobFolder}\n\n"
         + "Read job.json, prompt.md, status.md and existing logs first.\n"
         + "Reconstruct progress from files and continue implementation.\n"
         + "Do not ask what to do unless required files are missing.";

    /// <summary>
    /// Recovery prompt for the continue endpoint when the previous CLI session
    /// is lost or incompatible. Tells the agent to rebuild context from the
    /// job folder + git, then act on the user's follow-up that's appended at
    /// the bottom. Bounded ("last ~200 lines") so we don't ask the agent to
    /// drag a massive log into context. English on purpose — works across all
    /// supported CLIs.
    /// </summary>
    public static string BuildRecoveryContinuationPrompt(string jobFolder, string userFollowup)
        => "Continue this task — the previous CLI session was lost and cannot be resumed.\n\n"
         + "Task folder:\n"
         + $"{jobFolder}\n\n"
         + "Reconstruct context before doing anything else:\n"
         + "1. Read job.json, prompt.md, status.md.\n"
         + "2. Read the last ~200 lines of logs/cli-output.log to see what the previous run was doing.\n"
         + "3. Run `git status` and `git diff` in the working directory to see the current change set.\n\n"
         + "Then continue with the user's follow-up below. Treat this as a continuation, not a new task — keep the existing changes and protocol, append rather than restart.\n\n"
         + "User follow-up:\n"
         + (userFollowup?.TrimEnd() ?? string.Empty);
}
