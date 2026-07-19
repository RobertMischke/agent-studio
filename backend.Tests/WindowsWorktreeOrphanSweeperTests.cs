using AgentStudio.Runner;
using Xunit;

namespace AgentStudio.Tests;

public sealed class WindowsWorktreeOrphanSweeperTests
{
    [Fact]
    public void BuildTaskKillStartInfo_UsesForcedWholeTreeTermination()
    {
        var startInfo = WindowsWorktreeOrphanSweeper.BuildTaskKillStartInfo(16208);

        Assert.Equal("taskkill.exe", startInfo.FileName);
        Assert.Equal(new[] { "/F", "/T", "/PID", "16208" }, startInfo.ArgumentList);
        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
    }

    [Fact]
    public void SelectCandidates_OnlyReturnsDetachedHelpersBoundToTaskWorktrees()
    {
        var processes = new[]
        {
            new WindowsWorktreeOrphanSweeper.ProcessSnapshot(10, "node.exe", @"node C:\Temp\ass-worktrees\Project\task-a\node_modules\@angular\cli ng serve", null),
            new WindowsWorktreeOrphanSweeper.ProcessSnapshot(11, "esbuild.exe", null, @"C:\Temp\ass-worktrees\Project\task-a\node_modules\esbuild.exe"),
            new WindowsWorktreeOrphanSweeper.ProcessSnapshot(12, "node.exe", @"node C:\Projects\normal\server.js", null),
            new WindowsWorktreeOrphanSweeper.ProcessSnapshot(13, "dotnet.exe", @"dotnet C:\Temp\ass-worktrees\Project\backend.dll", null),
            new WindowsWorktreeOrphanSweeper.ProcessSnapshot(99, "node.exe", @"node C:\Temp\ass-worktrees\Project\self.js", null),
        };

        var candidates = WindowsWorktreeOrphanSweeper.SelectCandidates(processes, currentProcessId: 99);

        Assert.Equal(new[] { 10, 11 }, candidates.Select(p => p.ProcessId));
    }
}
