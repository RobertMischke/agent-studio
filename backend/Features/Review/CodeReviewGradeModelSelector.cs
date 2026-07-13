
namespace AgentStudio.Review;

/// <summary>
/// Resolves the model and CLI the automatic quality-grade code-review step
/// (ASS-1657) runs with. The grade pass is deliberately quality-first: it
/// follows the strongest model advertised by the live Codex catalogue while
/// the four bounded aspect reviews use the economy Codex model. Both the model and the CLI are
/// configurable (<c>CodeReviewStep:DefaultModel</c> / <c>CodeReviewStep:DefaultCli</c>)
/// so a deployment can dial the grade model without touching the aspects.
/// Extracted from the inline orchestrator path so the default is unit-testable.
/// </summary>
public static class CodeReviewGradeModelSelector
{
    /// <summary>
    /// Quality-first default model for the grade pass. Live Codex discovery may
    /// promote this to a newer flagship; gpt-5.5 is the safe static fallback.
    /// </summary>
    public static string DefaultModel =>
        ModelMetadataRegistry.DefaultForCli(CliTypes.Codex) ?? ModelIds.Gpt55;

    /// <summary>Default CLI for the grade pass.</summary>
    public const string DefaultCli = CliTypes.Codex;

    /// <summary>Top reasoning level advertised for the selected flagship.</summary>
    public static string? DefaultThinkingLevel =>
        ModelMetadataRegistry.DefaultThinkingLevelForCli(DefaultCli, DefaultModel);

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
