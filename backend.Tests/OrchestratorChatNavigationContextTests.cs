using System.Text;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Regression coverage for the navigation-context block the project chat
/// passes from the frontend into the global orchestrator prompt. Before
/// this surface existed the chat agent answered context-dependent
/// questions ("what is the current task?") in vacuum and hallucinated
/// freely - the 2026-05-09 "Conversation, Foul Conversation, blablabla"
/// incident. These tests pin:
///
///   1. The SendOrchestratorChatRequest record accepts a navigationContext
///      payload and tolerates absence (defaults to null).
///   2. The rendered prompt block names every field the frontend sends
///      (currentPage, currentTaskId, currentTaskTitle, currentTaskState,
///      currentLaneFilter, viewportTimestamp).
///   3. When the operator is on a task page, the prompt instructs the
///      agent to answer about that task and forbids invention.
///   4. When the operator is NOT on a task page (or context is missing),
///      the prompt instructs the agent to say so instead of hallucinating.
///
/// The structural unit test is necessary, not sufficient (per ADR-0007);
/// the live behavioural check sits in the Playwright spec.
/// </summary>
public class OrchestratorChatNavigationContextTests
{
    [Fact]
    public void SendRequest_AcceptsNavigationContext()
    {
        var nav = new ChatNavigationContext(
            CurrentPage: "task-detail",
            CurrentTaskId: "bug-X",
            CurrentTaskKey: "AGT-2517",
            CurrentTaskTitle: "Bug: reordering drops the card",
            CurrentTaskState: "4-auto-review");

        var req = new SendOrchestratorChatRequest(
            Text: "what is the current task?",
            Attachments: null,
            NavigationContext: nav);

        Assert.NotNull(req.NavigationContext);
        Assert.Equal("task-detail", req.NavigationContext!.CurrentPage);
        Assert.Equal("bug-X", req.NavigationContext.CurrentTaskId);
        Assert.Equal("AGT-2517", req.NavigationContext.CurrentTaskKey);
        Assert.Equal("Bug: reordering drops the card", req.NavigationContext.CurrentTaskTitle);
        Assert.Equal("4-auto-review", req.NavigationContext.CurrentTaskState);
    }

    [Fact]
    public void SendRequest_NavigationContext_IsOptional()
    {
        var req = new SendOrchestratorChatRequest(
            Text: "hello",
            Attachments: null);

        Assert.Null(req.NavigationContext);
    }

    [Fact]
    public void AppendNavigationContext_OnTaskDetail_RendersAllFields()
    {
        var nav = new ChatNavigationContext(
            CurrentPage: "task-detail",
            CurrentTaskId: "bug-auto-review-reorder-drops-card",
            CurrentTaskKey: "AGT-2517",
            CurrentTaskTitle: "Bug: reordering a card inside auto-review drops it from the lane",
            CurrentTaskState: "4-auto-review",
            CurrentLaneFilter: "4-auto-review",
            ViewportTimestamp: "2026-05-09T08:42:00Z");

        var sb = new StringBuilder();
        OrchestratorChatService.AppendNavigationContext(sb, nav);
        var rendered = sb.ToString();

        Assert.Contains("=== NAVIGATION CONTEXT ===", rendered);
        Assert.Contains("currentPage: task-detail", rendered);
        Assert.Contains("currentTaskId: bug-auto-review-reorder-drops-card", rendered);
        Assert.Contains("currentTaskKey: AGT-2517", rendered);
        Assert.Contains("currentTaskTitle: Bug: reordering a card inside auto-review drops it from the lane", rendered);
        Assert.Contains("currentTaskState: 4-auto-review", rendered);
        Assert.Contains("currentLaneFilter: 4-auto-review", rendered);
        Assert.Contains("viewportTimestamp: 2026-05-09T08:42:00Z", rendered);
        // The agent must be told to use the field and to refuse hallucination.
        Assert.Contains("currentTaskId", rendered);
        Assert.Contains("do NOT invent", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendNavigationContext_NullContext_InstructsAgentToAsk()
    {
        var sb = new StringBuilder();
        OrchestratorChatService.AppendNavigationContext(sb, null);
        var rendered = sb.ToString();

        Assert.Contains("=== NAVIGATION CONTEXT ===", rendered);
        Assert.Contains("No navigation context", rendered);
        Assert.Contains("do NOT invent", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendNavigationContext_AllFieldsBlank_TreatedAsAbsent()
    {
        var nav = new ChatNavigationContext(
            CurrentPage: "   ",
            CurrentTaskId: null,
            CurrentTaskTitle: "",
            CurrentTaskState: null);

        var sb = new StringBuilder();
        OrchestratorChatService.AppendNavigationContext(sb, nav);
        var rendered = sb.ToString();

        Assert.Contains("No navigation context", rendered);
    }

    [Fact]
    public void AppendNavigationContext_KanbanBoardWithoutTask_OmitsTaskFields()
    {
        var nav = new ChatNavigationContext(
            CurrentPage: "kanban-board",
            CurrentLaneFilter: "3-progress");

        var sb = new StringBuilder();
        OrchestratorChatService.AppendNavigationContext(sb, nav);
        var rendered = sb.ToString();

        Assert.Contains("currentPage: kanban-board", rendered);
        Assert.Contains("currentLaneFilter: 3-progress", rendered);
        Assert.DoesNotContain("currentTaskId:", rendered);
        Assert.DoesNotContain("currentTaskTitle:", rendered);
        // Still asks the agent to refuse invention when no task is in scope.
        Assert.Contains("do NOT invent", rendered, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void AppendNavigationContext_RepositoryPage_RendersPageReferenceAndExcerpt()
    {
        var nav = new ChatNavigationContext(
            CurrentPage: "repository-page",
            PageRef: "page:PROJ-002/concepts/action-bar.md",
            PageTitle: "Action bar",
            PageType: "concept",
            PageExcerpt: "Pages are bidirectional interfaces.");

        var sb = new StringBuilder();
        OrchestratorChatService.AppendNavigationContext(sb, nav);
        var rendered = sb.ToString();

        Assert.Contains("pageRef: page:PROJ-002/concepts/action-bar.md", rendered);
        Assert.Contains("pageTitle: Action bar", rendered);
        Assert.Contains("pageType: concept", rendered);
        Assert.Contains("pageExcerpt: Pages are bidirectional interfaces.", rendered);
        Assert.Contains("asking from THAT repository page", rendered);
    }
}
