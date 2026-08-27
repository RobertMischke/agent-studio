using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// Card-cutting policy for approved Dossier recommendations. One descriptor
/// item is one independently reviewable slice and therefore one coding card.
/// An open-ended "implement all recommendations" item is rejected unless the
/// descriptor supplies an explicit bounded-slice acceptance scope.
/// </summary>
public static class DossierImplementationCardPolicy
{
    private static readonly string[] OpenEndedPhrases =
    [
        "implement all recommendations",
        "implement every recommendation",
        "deliver all recommendations",
        "complete all recommendations",
    ];

    private static readonly Regex AllRecommendations = new(
        @"\b(?:implement|deliver|complete)\s+(?:all|every)\b.{0,80}\brecommendations?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    public static TaskAcceptanceScope AcceptanceScopeFor(ConceptImplementationTask item)
    {
        var explicitScope = TaskAcceptanceScopes.Normalize(item.AcceptanceScope);
        if (item.AcceptanceScope is not null && explicitScope is null)
            throw new InvalidDataException(
                "A Dossier implementation item's acceptanceScope is invalid or incomplete.");
        if (explicitScope is not null)
        {
            if (explicitScope.DeliveryMode != TaskAcceptanceDeliveryModes.BoundedSlice)
                throw new InvalidDataException(
                    "A Dossier implementation item must use acceptanceScope.deliveryMode 'bounded-slice'.");
            return explicitScope;
        }

        if (IsOpenEnded(item))
            throw new InvalidDataException(
                "A Dossier cannot promote an open-ended 'implement all recommendations' item. " +
                "Cut one implementationTasks entry per slice and give each entry a bounded acceptanceScope.");

        return TaskAcceptanceScopes.BoundedSlice(
            item.Title,
            item.PromptMarkdown.Trim());
    }

    public static string? Validate(ConceptImplementationTask item)
    {
        try
        {
            _ = AcceptanceScopeFor(item);
            return null;
        }
        catch (InvalidDataException ex)
        {
            return ex.Message;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    private static bool IsOpenEnded(ConceptImplementationTask item)
    {
        var text = $"{item.Title}\n{item.PromptMarkdown}";
        return AllRecommendations.IsMatch(text)
               || OpenEndedPhrases.Any(phrase =>
                   text.Contains(phrase, StringComparison.OrdinalIgnoreCase));
    }
}
