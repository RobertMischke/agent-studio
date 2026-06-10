
namespace AgentStudio.Review;

/// <summary>
/// Resolves the model and CLI the automatic quality-grade code-review step
/// (ASS-1657) runs with. The grade pass is deliberately quality-first: it
/// defaults to Claude Opus 4.8 even though the four cheap aspect reviews stay
/// on Haiku (<see cref="AgentStudio.Runner.OrchestratorRunner.DefaultModel"/>).
/// That asymmetry is the whole point — it resolves the ASS-855 (pulled the
/// review onto Haiku for cost) vs ASS-916 (wanted it back on Opus) tension:
/// the cheap aspect verdicts keep running on Haiku, but the operator-facing
/// quality grade gets the strong model. Both the model and the CLI are
/// configurable (<c>CodeReviewStep:DefaultModel</c> / <c>CodeReviewStep:DefaultCli</c>)
/// so a deployment can dial the grade model without touching the aspects.
/// Extracted from the inline orchestrator path so the default is unit-testable.
/// </summary>
public static class CodeReviewGradeModelSelector
{
    /// <summary>
    /// Quality-first default model for the grade pass: Claude Opus 4.8.
    /// Intentionally distinct from the cheap aspect default (Haiku); the
    /// regression test pins this so a future cost cut can't silently drag the
    /// grade pass back onto a weak model.
    /// </summary>
    public const string DefaultModel = ClaudeCliService.DefaultOpusModel;

    /// <summary>Default CLI for the grade pass.</summary>
    public const string DefaultCli = "claude";

    /// <summary>
    /// Resolve the effective (model, cli) for the grade pass, layering the
    /// per-deployment config over the quality-first defaults. A blank or
    /// whitespace-only override falls back to the default rather than running
    /// the grade on an empty model id.
    /// </summary>
    public static (string Model, string Cli) Resolve(string? configuredModel, string? configuredCli)
    {
        var model = string.IsNullOrWhiteSpace(configuredModel)
            ? DefaultModel
            : configuredModel.Trim();
        var cli = string.IsNullOrWhiteSpace(configuredCli)
            ? DefaultCli
            : configuredCli.Trim();
        return (model, cli);
    }
}
