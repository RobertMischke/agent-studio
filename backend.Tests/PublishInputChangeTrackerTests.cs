using AgentStudio.Publishing;

using Xunit;

namespace AgentStudio.Tests;

public sealed class PublishInputChangeTrackerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "publish-input-tracker-" + Guid.NewGuid().ToString("N"));

    public PublishInputChangeTrackerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Capture_RecreatedWatcherUsesNewEpochAfterEviction()
    {
        var firstRoot = CreateRepositoryDirectory("first");
        var secondRoot = CreateRepositoryDirectory("second");
        using var tracker = new PublishInputChangeTracker(maxWatchedRepositories: 1);

        var before = tracker.Capture(firstRoot);
        tracker.Capture(secondRoot); // evicts the now lease-free first watcher
        WriteFile(firstRoot, "src/Nested/App.csproj", "<Project />");

        var after = tracker.Capture(firstRoot);

        Assert.NotEqual(before.Value, after.Value);
        Assert.False(after.RequiresShortFallback);
    }

    [Fact]
    public void Capture_AfterWatcherErrorRecreatesReliableWatcher()
    {
        var repo = CreateRepositoryDirectory("error");
        using var tracker = new PublishInputChangeTracker();
        var before = tracker.Capture(repo);

        tracker.SimulateWatcherErrorForTests(repo);
        var after = tracker.Capture(repo);

        Assert.NotEqual(before.Value, after.Value);
        Assert.False(after.RequiresShortFallback);
    }

    [Fact]
    public void Capture_DottedDirectoryStructuralEventInvalidates()
    {
        var repo = CreateRepositoryDirectory("dotted");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        using var tracker = new PublishInputChangeTracker();
        var before = tracker.Capture(repo);

        Directory.CreateDirectory(Path.Combine(repo, "src", "package.v2"));

        Assert.True(SpinWait.SpinUntil(
            () => tracker.Capture(repo).Value != before.Value,
            TimeSpan.FromSeconds(5)));
    }

    private string CreateRepositoryDirectory(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteFile(string root, string relative, string content)
    {
        var path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
