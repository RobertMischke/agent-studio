using System.Text.RegularExpressions;

namespace AgentStudio.Shared;

/// <summary>
/// Pure classifier for the one steer-question shape the steer-timeout resolver
/// can answer deterministically from the branch state: <b>"is this already
/// implemented / done / merged?"</b>. This is the 2067 evidence class - the
/// follow-up run asked "ist iframe schon implementiert?" while its work was long
/// since merged, so the answer was derivable from the branch/develop state
/// (concept Rule 2, the named auto-answer case).
///
/// <para>
/// Deliberately narrow and conservative: it only recognises the
/// already-done family of questions. Anything else returns false so the
/// resolver escalates (blocked) rather than guessing - "when unsure, escalate,
/// never wait forever". Kept pure (regex over the question text) so it is fully
/// unit-testable and carries no I/O.
/// </para>
/// </summary>
public static class SteerQuestionClassifier
{
    // "already" + a done-ish verb, OR a done-ish verb + "already", within a
    // short window, anchored on the question being ABOUT existing state rather
    // than a request to do new work. Case-insensitive; tolerant of the
    // question mark being absent (agents phrase these as statements too).
    private static readonly Regex AlreadyDone = new(
        @"\balready\b[\w\s,'""./()-]{0,60}?\b(implement|implemented|done|built|merged|integrat|exist|in\s+(develop|main|dev)|there|present|complete|finished|shipped|landed)\w*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex DoneAlready = new(
        @"\b(implement|implemented|done|built|merged|integrat|exist|complete|finished|shipped|landed)\w*[\w\s,'""./()-]{0,40}?\balready\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // "is X (already) in develop / main" style, without the literal "already".
    private static readonly Regex InIntegration = new(
        @"\b(is|are|was|were|has|have|does|do)\b[\w\s,'""./()-]{0,60}?\b(already\s+)?(merged|integrat\w*|landed|shipped|in)\b[\w\s,'""./()-]{0,20}?\b(develop|main|dev|the\s+integration\s+branch|the\s+work\s+branch)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// True when <paramref name="question"/> asks whether some work is already
    /// implemented / done / merged - the class the branch-state check can
    /// answer. False (the safe default) for every other question so the
    /// resolver escalates instead of guessing.
    /// </summary>
    public static bool IsAlreadyImplementedQuestion(string? question)
    {
        if (string.IsNullOrWhiteSpace(question)) return false;
        var q = question.Trim();
        return AlreadyDone.IsMatch(q) || DoneAlready.IsMatch(q) || InIntegration.IsMatch(q);
    }
}
