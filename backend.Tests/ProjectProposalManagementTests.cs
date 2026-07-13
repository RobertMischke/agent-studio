using AgentStudio.Proposals;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ProjectProposalManagementTests
{
    [Fact]
    public void CliDraftParser_NormalizesTheStructuredProposal()
    {
        var draft = ProjectProposalDraftingService.ParseDraft("""
            ```json
            {"finding":"Responsiveness is not represented in the current narrow layout.","proposal":"Make responsiveness explicit and collapse secondary navigation.","estimatedEffort":"MEDIUM","severity":"critical","categories":["responsiveness","navigation"]}
            ```
            """);

        Assert.Equal("medium", draft.EstimatedEffort);
        Assert.Equal("critical", draft.Severity);
        Assert.Equal(["responsiveness", "navigation"], draft.Categories);
    }

    [Fact]
    public void MarkdownHistory_PreservesTopicSourceAndBothFeedbackForms()
    {
        var proposal = new ProjectProposal(
            "survey-001", "2026-07-13", "Narrow layouts clip the content.", "assets/001.png",
            "Collapse secondary navigation on narrow layouts.", "medium", "critical", "rejected", null,
            "Responsiveness", ["responsiveness", "navigation"], "Visual survey: narrow.png",
            "The proposal must identify responsiveness as its governing topic.",
            "Da muss ganz klar Responsiveness stehen.", "2026-07-13/survey-001.md", DateTime.UtcNow);

        var markdown = ProjectProposalService.Render(proposal);

        Assert.Contains("topic: \"Responsiveness\"", markdown);
        Assert.Contains("## Rejection feedback", markdown);
        Assert.Contains("Da muss ganz klar Responsiveness stehen.", markdown);
    }
}
