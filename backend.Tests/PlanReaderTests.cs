using OrchestratorApi.Models;
using OrchestratorApi.Services.Tasks;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the passive plan replay in <see cref="PlanReader"/>. Each test writes
/// real on-disk fixture shapes (the <c>logs/plan-snapshots.jsonl</c> and
/// <c>logs/tool-calls.jsonl</c> the runtime already appends) into a throwaway
/// folder, then asserts the folded <see cref="TaskPlanView"/>. No process, no
/// LLM: this is the same fold the GET /plan endpoint runs.
/// </summary>
public sealed class PlanReaderTests : IDisposable
{
    private readonly string _root;

    public PlanReaderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "planreader-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(_root, "logs"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    private TaskInfo Info() => new() { FolderPath = _root };

    private void WriteSnapshots(params string[] lines)
        => File.WriteAllLines(Path.Combine(_root, "logs", "plan-snapshots.jsonl"), lines);

    private void WriteToolCalls(params string[] lines)
        => File.WriteAllLines(Path.Combine(_root, "logs", "tool-calls.jsonl"), lines);

    private static string Snap(string ts, string source, params (string id, string title, string status)[] items)
    {
        var parts = items.Select(i =>
            $$"""{"id":"{{i.id}}","title":"{{i.title}}","status":"{{i.status}}"}""");
        return $$"""{"ts":"{{ts}}","seq":1,"source":"{{source}}","items":[{{string.Join(",", parts)}}]}""";
    }

    private static string Started(string ts, string tool, string? argument = null)
        => argument == null
            ? $$"""{"ts":"{{ts}}","kind":"started","tool":"{{tool}}"}"""
            : $$"""{"ts":"{{ts}}","kind":"started","tool":"{{tool}}","argument":"{{argument}}"}""";

    [Fact]
    public void NoSnapshotsFile_ReturnsHasPlanFalse()
    {
        var view = PlanReader.Read(Info());
        Assert.False(view.HasPlan);
        Assert.Empty(view.Items);
    }

    [Fact]
    public void EmptySnapshotsFile_ReturnsHasPlanFalse()
    {
        WriteSnapshots(); // zero lines
        var view = PlanReader.Read(Info());
        Assert.False(view.HasPlan);
    }

    [Fact]
    public void AttributesToolCallsToActiveItem_AndBucketsPrePlanCalls()
    {
        WriteSnapshots(
            Snap("2026-05-31T10:00:01Z", "claude/TodoWrite", ("aaaa1111", "Survey the repo", "active"), ("bbbb2222", "Write the patch", "pending")),
            Snap("2026-05-31T10:00:04Z", "claude/TodoWrite", ("aaaa1111", "Survey the repo", "done"), ("bbbb2222", "Write the patch", "active")),
            Snap("2026-05-31T10:00:07Z", "claude/TodoWrite", ("aaaa1111", "Survey the repo", "done"), ("bbbb2222", "Write the patch", "done")));
        WriteToolCalls(
            Started("2026-05-31T10:00:00Z", "Read", "foo.cs"),   // before first plan -> unassigned
            Started("2026-05-31T10:00:02Z", "Grep", "needle"),   // itemA active
            Started("2026-05-31T10:00:03Z", "Read", "baz.cs"),   // itemA active
            Started("2026-05-31T10:00:05Z", "Edit", "qux.cs"),   // itemB active
            Started("2026-05-31T10:00:06Z", "TodoWrite"));       // plan-frame tool -> never a sub-action

        var view = PlanReader.Read(Info());

        Assert.True(view.HasPlan);
        Assert.Equal("claude/TodoWrite", view.Source);
        Assert.Equal(3, view.SnapshotCount);
        Assert.Equal(2, view.Items.Count);

        var a = view.Items.Single(i => i.Id == "aaaa1111");
        var b = view.Items.Single(i => i.Id == "bbbb2222");
        Assert.Equal(2, a.SubActionCount);
        Assert.Equal(1, b.SubActionCount);
        Assert.Equal("Survey the repo", a.Title);

        // The pre-plan Read lands in the unassigned bucket, not on any item.
        var unassigned = Assert.Single(view.UnassignedSubActions);
        Assert.Equal("Read", unassigned.Tool);
        Assert.Equal("Read foo.cs", unassigned.Label);

        // TodoWrite is never counted as a sub-action anywhere.
        Assert.DoesNotContain(view.Items.SelectMany(i => i.SubActions), s => s.Tool == "TodoWrite");
    }

    [Fact]
    public void SoftEstimateMedian_RequiresTwoDoneItems()
    {
        // One done item only -> median suppressed.
        WriteSnapshots(
            Snap("2026-05-31T10:00:01Z", "claude/TodoWrite", ("aaaa1111", "A", "active"), ("bbbb2222", "B", "active")),
            Snap("2026-05-31T10:00:05Z", "claude/TodoWrite", ("aaaa1111", "A", "done"), ("bbbb2222", "B", "active")));
        WriteToolCalls(
            Started("2026-05-31T10:00:02Z", "Read", "a.cs"),
            Started("2026-05-31T10:00:03Z", "Read", "b.cs"));

        Assert.Null(PlanReader.Read(Info()).SoftEstimateMedian);
    }

    [Fact]
    public void SoftEstimateMedian_IsMedianOfDoneSubActionCounts()
    {
        // itemA done with 2 sub-actions, itemB done with 1 -> median of [2,1] = 2.
        WriteSnapshots(
            Snap("2026-05-31T10:00:01Z", "claude/TodoWrite", ("aaaa1111", "A", "active"), ("bbbb2222", "B", "pending")),
            Snap("2026-05-31T10:00:04Z", "claude/TodoWrite", ("aaaa1111", "A", "done"), ("bbbb2222", "B", "active")),
            Snap("2026-05-31T10:00:07Z", "claude/TodoWrite", ("aaaa1111", "A", "done"), ("bbbb2222", "B", "done")));
        WriteToolCalls(
            Started("2026-05-31T10:00:02Z", "Grep", "x"),
            Started("2026-05-31T10:00:03Z", "Read", "y.cs"),
            Started("2026-05-31T10:00:05Z", "Edit", "z.cs"));

        Assert.Equal(2, PlanReader.Read(Info()).SoftEstimateMedian);
    }

    [Fact]
    public void StreamingDuplicateToolStarts_AreCollapsed()
    {
        WriteSnapshots(
            Snap("2026-05-31T10:00:01Z", "claude/TodoWrite", ("aaaa1111", "A", "active")));
        // Same tool + argument twice within one second is one logical call.
        WriteToolCalls(
            Started("2026-05-31T10:00:02.000Z", "Read", "dup.cs"),
            Started("2026-05-31T10:00:02.400Z", "Read", "dup.cs"),
            Started("2026-05-31T10:00:04.000Z", "Read", "dup.cs")); // >1s later -> distinct

        var view = PlanReader.Read(Info());
        var a = view.Items.Single(i => i.Id == "aaaa1111");
        Assert.Equal(2, a.SubActionCount);
    }

    [Fact]
    public void PathArgument_LabelShowsLeafOnly()
    {
        WriteSnapshots(
            Snap("2026-05-31T10:00:01Z", "claude/TodoWrite", ("aaaa1111", "A", "active")));
        WriteToolCalls(
            Started("2026-05-31T10:00:02Z", "Read", "C:/some/deep/dir/file.cs"));

        var sub = Assert.Single(PlanReader.Read(Info()).Items.Single().SubActions);
        Assert.Equal("Read file.cs", sub.Label);
    }

    [Fact]
    public void ActiveItemId_ReflectsLatestSnapshot()
    {
        WriteSnapshots(
            Snap("2026-05-31T10:00:01Z", "codex/update_plan", ("aaaa1111", "A", "active"), ("bbbb2222", "B", "pending")),
            Snap("2026-05-31T10:00:04Z", "codex/update_plan", ("aaaa1111", "A", "done"), ("bbbb2222", "B", "active")));

        var view = PlanReader.Read(Info());
        Assert.Equal("bbbb2222", view.ActiveItemId);
        Assert.Equal("codex/update_plan", view.Source);
    }

    [Fact]
    public void TornAndBlankLines_AreSkippedNotThrown()
    {
        WriteSnapshots(
            "",
            "{not valid json",
            Snap("2026-05-31T10:00:01Z", "claude/TodoWrite", ("aaaa1111", "A", "active")));
        WriteToolCalls(
            "garbage",
            "",
            Started("2026-05-31T10:00:02Z", "Read", "a.cs"));

        var view = PlanReader.Read(Info());
        Assert.True(view.HasPlan);
        Assert.Equal(1, view.Items.Single().SubActionCount);
    }
}
