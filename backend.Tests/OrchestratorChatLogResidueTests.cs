using Microsoft.Extensions.Logging.Abstractions;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Runner;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Regression for the 2026-05-16 incident where 4-auto-review accumulated
/// one-line "Moved to 5-human-review..." skeleton folders. Root cause: after a
/// successful lane move, the chat-log caller fell back to the pre-move TaskInfo
/// when the scanner cache had not yet refreshed, and
/// <see cref="OrchestratorChatLog.Append"/> unconditionally called
/// <c>Directory.CreateDirectory</c> on its target — resurrecting the source
/// folder with a single orphan log file.
///
/// Contract under test: when the job folder no longer exists on disk
/// (the move took it elsewhere, or the operator deleted it),
/// <c>Append</c> refuses the write and returns <c>false</c> instead of
/// recreating the path.
/// </summary>
public class OrchestratorChatLogResidueTests : IDisposable
{
    private readonly string _root;

    public OrchestratorChatLogResidueTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "chatlog-residue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void Append_VanishedFolder_RefusesWrite_DoesNotRecreatePath()
    {
        var ghostFolder = Path.Combine(_root, "4-auto-review", "moved-elsewhere");
        Assert.False(Directory.Exists(ghostFolder));

        var info = new TaskInfo
        {
            Id = "moved-elsewhere",
            Title = "Moved Elsewhere",
            State = TaskStates.AutoReview,
            FolderPath = ghostFolder,
            WatchPath = _root,
        };

        var log = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);

        var ok = log.Append(info, OrchestratorMessageKind.Decision,
            "Auto-review accepted \"Moved Elsewhere\" as done. Moved to 5-human-review for your approval.");

        Assert.False(ok);
        Assert.False(Directory.Exists(ghostFolder));
        Assert.False(File.Exists(Path.Combine(ghostFolder, "logs", "cli-output.log")));
    }

    [Fact]
    public void AppendSupervisor_VanishedFolder_RefusesWrite_DoesNotRecreatePath()
    {
        var ghostFolder = Path.Combine(_root, "4-auto-review", "escalated-elsewhere");
        Assert.False(Directory.Exists(ghostFolder));

        var info = new TaskInfo
        {
            Id = "escalated-elsewhere",
            Title = "Escalated Elsewhere",
            State = TaskStates.AutoReview,
            FolderPath = ghostFolder,
            WatchPath = _root,
        };

        var log = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);

        var ok = log.AppendSupervisor(info, "escalate",
            "Auto-review escalated \"Escalated Elsewhere\" to 5-human-review.");

        Assert.False(ok);
        Assert.False(Directory.Exists(ghostFolder));
    }

    [Fact]
    public void Append_LiveFolder_StillWritesNormally()
    {
        var liveFolder = Path.Combine(_root, "5-human-review", "still-here");
        Directory.CreateDirectory(liveFolder);

        var info = new TaskInfo
        {
            Id = "still-here",
            Title = "Still Here",
            State = TaskStates.HumanReview,
            FolderPath = liveFolder,
            WatchPath = _root,
        };

        var log = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);

        var ok = log.Append(info, OrchestratorMessageKind.Decision, "Hello after move.");

        Assert.True(ok);
        var logFile = Path.Combine(liveFolder, "logs", "cli-output.log");
        Assert.True(File.Exists(logFile));
        var content = File.ReadAllText(logFile);
        Assert.Contains("[orchestrator]", content);
        Assert.Contains("[decision]", content);
        Assert.Contains("Hello after move.", content);
    }

    [Fact]
    public void Append_IntegrationConflict_PersistsTaskVisibleMarkerAndDetails()
    {
        var liveFolder = Path.Combine(_root, "4-auto-review", "conflicted");
        Directory.CreateDirectory(liveFolder);

        var info = new TaskInfo
        {
            Id = "conflicted",
            Title = "Conflicted",
            State = TaskStates.AutoReview,
            FolderPath = liveFolder,
            WatchPath = _root,
        };

        var log = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);

        var ok = log.Append(info, OrchestratorMessageKind.IntegrationConflict,
            "Worktree branch integration is blocked by a merge conflict. Task branch `task/ASS-111` was not merged into `develop`. Worktree: `C:\\temp\\wt`. Conflicted files: frontend/src/app/tree.ts. Error: could not apply commit");

        Assert.True(ok);
        var content = File.ReadAllText(Path.Combine(liveFolder, "logs", "cli-output.log"));
        Assert.Contains("[orchestrator]", content);
        Assert.Contains("[integration-conflict]", content);
        Assert.Contains("task/ASS-111", content);
        Assert.Contains("frontend/src/app/tree.ts", content);
    }
}
