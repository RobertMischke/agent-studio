using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using AgentStudio.Cli;
using AgentStudio.Shared;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the per-CLI Working-Memory panel service (ASS-1748 / T1c). The panel is
/// the only surface that deletes a CLI's accumulated state, so the central
/// guarantee under test is the auth-safety one: memory / session state is
/// describable and deletable, while auth / credential and base-config files are
/// reported as protected and the delete endpoint refuses them. Tests build a
/// throwaway CLI config home, point the service at it via the home-resolver test
/// seam, and assert describe shape, preview redaction, and the delete guard.
/// </summary>
public class CliWorkingMemoryServiceTests : IDisposable
{
    private readonly string _home;

    public CliWorkingMemoryServiceTests()
    {
        _home = Path.Combine(Path.GetTempPath(), "cli-wm-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_home, recursive: true); } catch { /* best effort */ }
    }

    private CliWorkingMemoryService NewService() =>
        new(NullLogger<CliWorkingMemoryService>.Instance, () => _home);

    private void Write(string relUnderHome, string content)
    {
        var path = Path.Combine(_home, relUnderHome);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Describe_ListsExistingMemoryWithSizePreviewAndDeletableFlag()
    {
        Write(".claude/CLAUDE.md", "# user memory\nremember to be terse.");

        var report = NewService().Describe("claude");

        Assert.Equal("claude", report.CliType);
        Assert.True(report.Available);
        Assert.Equal(Path.Combine(_home, ".claude"), report.Root);

        var memory = Assert.Single(report.Entries, e => e.Kind == CliWorkingMemoryKinds.Memory);
        Assert.True(memory.Deletable);
        Assert.True(memory.SizeBytes > 0);
        Assert.NotNull(memory.LastModifiedUtc);
        Assert.Contains("remember to be terse", memory.Preview);
    }

    [Fact]
    public void Describe_OnlyListsStatesThatExistOnDisk()
    {
        // Nothing written: an absent CLAUDE.md / sessions dir must not appear.
        Write(".claude/settings.json", "{}"); // only the protected config exists

        var report = NewService().Describe("claude");

        Assert.DoesNotContain(report.Entries, e => e.Kind == CliWorkingMemoryKinds.Memory);
        Assert.Contains(report.Entries, e => e.Kind == CliWorkingMemoryKinds.Config);
    }

    [Fact]
    public void Describe_OrdersDeletableStatesBeforeProtected()
    {
        Write(".claude/CLAUDE.md", "mem");
        Write(".claude/.credentials.json", "{\"token\":\"secret\"}");
        Write(".claude/settings.json", "{}");

        var entries = NewService().Describe("claude").Entries;

        var firstProtected = entries.FindIndex(e => !e.Deletable);
        var lastDeletable = entries.FindLastIndex(e => e.Deletable);
        Assert.True(lastDeletable < firstProtected,
            "all deletable entries must be ordered before any protected entry");
    }

    [Fact]
    public void Describe_NeverPreviewsAuthCredentialBodies()
    {
        Write(".claude/.credentials.json", "{\"accessToken\":\"super-secret-value\"}");

        var report = NewService().Describe("claude");

        var auth = Assert.Single(report.Entries, e => e.Kind == CliWorkingMemoryKinds.Auth);
        Assert.False(auth.Deletable);
        Assert.Null(auth.Preview); // the secret body is never surfaced
    }

    [Fact]
    public void Describe_DirectorySessionStore_ReportsAggregateSizeAndCount()
    {
        Write(".claude/projects/projA/transcript-1.jsonl", "line one");
        Write(".claude/projects/projB/transcript-2.jsonl", "line two longer");

        var report = NewService().Describe("claude");

        var session = Assert.Single(
            report.Entries, e => e.Kind == CliWorkingMemoryKinds.Session && e.IsDirectory && e.Label == "Session store");
        Assert.True(session.Deletable);
        Assert.Equal(2, session.ItemCount);
        Assert.True(session.SizeBytes > 0);
    }

    [Fact]
    public void Delete_RemovesAMemoryFileAndReportsFreedBytes()
    {
        Write(".claude/CLAUDE.md", "delete me");
        var path = Path.Combine(_home, ".claude", "CLAUDE.md");
        var svc = NewService();

        var result = svc.Delete("claude", path);

        Assert.Equal(CliWorkingMemoryDeleteStatus.Deleted, result.Status);
        Assert.True(result.FreedBytes > 0);
        Assert.False(File.Exists(path));
        Assert.DoesNotContain(result.Report!.Entries, e => e.Kind == CliWorkingMemoryKinds.Memory);
    }

    [Fact]
    public void Delete_RefusesClaudeCredentialsFile_AsProtected()
    {
        Write(".claude/.credentials.json", "{\"token\":\"secret\"}");
        var path = Path.Combine(_home, ".claude", ".credentials.json");
        var svc = NewService();

        var result = svc.Delete("claude", path);

        Assert.Equal(CliWorkingMemoryDeleteStatus.Protected, result.Status);
        Assert.True(File.Exists(path), "auth credentials must never be deleted");
    }

    [Fact]
    public void Delete_RefusesGlobalConfig_AsProtected()
    {
        Write(".claude/settings.json", "{\"theme\":\"dark\"}");
        var path = Path.Combine(_home, ".claude", "settings.json");
        var svc = NewService();

        var result = svc.Delete("claude", path);

        Assert.Equal(CliWorkingMemoryDeleteStatus.Protected, result.Status);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Delete_RefusesCodexAuthJson_AsProtected()
    {
        var saved = Environment.GetEnvironmentVariable("CODEX_HOME");
        Environment.SetEnvironmentVariable("CODEX_HOME", null);
        try
        {
            Write(".codex/auth.json", "{\"OPENAI_API_KEY\":\"secret\"}");
            var path = Path.Combine(_home, ".codex", "auth.json");
            var svc = NewService();

            var result = svc.Delete("codex", path);

            Assert.Equal(CliWorkingMemoryDeleteStatus.Protected, result.Status);
            Assert.True(File.Exists(path), "codex auth.json must never be deleted");
        }
        finally
        {
            Environment.SetEnvironmentVariable("CODEX_HOME", saved);
        }
    }

    [Fact]
    public void Delete_RefusesUnknownOrOutOfRootPath_AsNotFound()
    {
        Write(".claude/CLAUDE.md", "mem");
        var outside = Path.Combine(_home, "..", "etc-passwd-ish");
        var svc = NewService();

        var result = svc.Delete("claude", outside);

        Assert.Equal(CliWorkingMemoryDeleteStatus.NotFound, result.Status);
    }

    [Fact]
    public void Delete_EmptyPath_IsNotFound_NotAnUnhandledThrow()
    {
        var result = NewService().Delete("claude", "   ");
        Assert.Equal(CliWorkingMemoryDeleteStatus.NotFound, result.Status);
        Assert.NotNull(result.Report);
    }
}
