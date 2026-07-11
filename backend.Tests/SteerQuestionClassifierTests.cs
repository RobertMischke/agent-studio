using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The narrow "is this already implemented / done / merged?" classifier that the
/// default steer-timeout resolver uses to decide whether the branch-state check
/// can answer a question (the 2067 evidence class). It must fire on the
/// already-done family and stay conservative (false) on everything else so the
/// resolver escalates rather than guessing.
/// </summary>
public sealed class SteerQuestionClassifierTests
{
    [Theory]
    [InlineData("Is the iframe embed already implemented in develop?")]
    [InlineData("is this already implemented?")]
    [InlineData("Has the dark-mode toggle already been built?")]
    [InlineData("Is the export feature already there, or should I add it?")]
    [InlineData("The reviewer asked whether this was already merged into develop.")]
    [InlineData("Was this already done in a previous task?")]
    [InlineData("is the sidebar already integrated into main")]
    [InlineData("ist iframe schon implementiert?")]
    [InlineData("Ist das Feature bereits umgesetzt?")]
    [InlineData("Wurde der Export schon gemergt?")]
    public void RecognizesAlreadyImplementedQuestions(string q)
        => Assert.True(SteerQuestionClassifier.IsAlreadyImplementedQuestion(q));

    [Theory]
    [InlineData("Should I use Postgres or SQLite for this?")]
    [InlineData("Which color should the button be?")]
    [InlineData("Do you want me to also refactor the helper?")]
    [InlineData("What is the expected behaviour on error?")]
    [InlineData("Please confirm the API contract before I continue.")]
    [InlineData("")]
    [InlineData(null)]
    public void RejectsOpenEndedOrDesignQuestions(string? q)
        => Assert.False(SteerQuestionClassifier.IsAlreadyImplementedQuestion(q));
}
