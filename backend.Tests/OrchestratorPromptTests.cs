

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the orchestrator-decision prompt content. The orchestrator runs
/// against the user's task with whatever context the prompt gives it; the
/// load-bearing piece for tasks whose entire context lives in a screenshot
/// is the attachments listing. Without it, the orchestrator decides blind
/// and falls back to BLOCK on every visual ambiguity.
/// </summary>
public class OrchestratorPromptTests
{
    private static TaskInfo Job(string title = "Layout looks off") => new()
    {
        Id = "fix-layout",
        Title = title,
        ProjectName = "demo-app",
        FolderPath = @"C:\jobs\fix-layout"
    };

    [Fact]
    public void OneShotPrompt_NoAttachments_OmitsAttachmentsBlock()
    {
        var prompt = ProjectRunner.BuildOrchestratorPrompt(
            Job(),
            promptText: "fix the spacing",
            lastAgentText: "Should rows be evenly distributed?",
            attachmentsList: "(none)");

        Assert.DoesNotContain("Attachments on this task", prompt, System.StringComparison.Ordinal);
        Assert.Contains("BLOCK", prompt, System.StringComparison.Ordinal);
    }

    [Fact]
    public void OneShotPrompt_WithScreenshot_ListsItAndTellsOrchestratorToReadItFirst()
    {
        const string list = "- `screenshot.png` → `C:\\jobs\\fix-layout\\attachments\\screenshot.png`";
        var prompt = ProjectRunner.BuildOrchestratorPrompt(
            Job(),
            promptText: "Die Zeilen sind nicht gleichmaessig verteilt",
            lastAgentText: "Which column is wrong? Could you mark it?",
            attachmentsList: list);

        Assert.Contains("Attachments on this task", prompt, System.StringComparison.Ordinal);
        Assert.Contains("screenshot.png", prompt, System.StringComparison.Ordinal);
        Assert.Contains("C:\\jobs\\fix-layout\\attachments\\screenshot.png", prompt, System.StringComparison.Ordinal);
        Assert.Contains("Read tool", prompt, System.StringComparison.Ordinal);
        Assert.Contains("Before answering BLOCK", prompt, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ResumePrompt_NoAttachments_OmitsAttachmentsBlock()
    {
        var prompt = ProjectRunner.BuildOrchestratorResumePrompt(
            Job(), lastAgentText: "any preference?", attachmentsList: "(none)");

        Assert.DoesNotContain("Attachments on this task", prompt, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ResumePrompt_WithAttachment_IncludesReadHint()
    {
        const string list = "- `mockup.png` → `C:\\jobs\\fix-layout\\attachments\\mockup.png`";
        var prompt = ProjectRunner.BuildOrchestratorResumePrompt(
            Job(), lastAgentText: "any preference?", attachmentsList: list);

        Assert.Contains("Attachments on this task", prompt, System.StringComparison.Ordinal);
        Assert.Contains("mockup.png", prompt, System.StringComparison.Ordinal);
        Assert.Contains("Read tool", prompt, System.StringComparison.Ordinal);
    }

    [Fact]
    public void BuildAttachmentsList_NonExistentFolder_ReturnsNone()
    {
        var list = ProjectRunner.BuildAttachmentsList(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"orch-prompt-test-{System.Guid.NewGuid():N}"));
        Assert.Equal("(none)", list);
    }

    [Fact]
    public void BuildAttachmentsList_FolderWithFiles_EmitsAbsolutePaths()
    {
        var jobFolder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"orch-prompt-test-{System.Guid.NewGuid():N}");
        var attachmentsDir = System.IO.Path.Combine(jobFolder, "attachments");
        System.IO.Directory.CreateDirectory(attachmentsDir);
        try
        {
            var imagePath = System.IO.Path.Combine(attachmentsDir, "shot.png");
            System.IO.File.WriteAllBytes(imagePath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

            var list = ProjectRunner.BuildAttachmentsList(jobFolder);

            Assert.Contains("shot.png", list, System.StringComparison.Ordinal);
            Assert.Contains(imagePath, list, System.StringComparison.Ordinal);
        }
        finally
        {
            try { System.IO.Directory.Delete(jobFolder, recursive: true); } catch { }
        }
    }
}
