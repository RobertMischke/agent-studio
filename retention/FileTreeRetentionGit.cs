using System.Diagnostics;
using System.Text;

namespace AgentStudio.Retention;

public static class FileTreeRetentionGit
{
    public static void RequireCleanIndex(string workspace)
    {
        if (!Directory.Exists(Path.Combine(workspace, ".git"))) return;
        if (RunCode(workspace, ["diff", "--cached", "--quiet"]) != 0)
            throw new InvalidOperationException("Retention apply requires a clean Git index so scoped evidence commits cannot absorb staged operator changes.");
    }

    public static void EnsureRuntimeIgnored(string workspace)
    {
        if (!Directory.Exists(Path.Combine(workspace, ".git"))) return;
        var ignorePath = Path.Combine(workspace, ".gitignore");
        var text = File.Exists(ignorePath) ? File.ReadAllText(ignorePath) : string.Empty;
        if (!text.Replace("\\", "/").Split('\n').Any(line => line.Trim() == "logs/bus/"))
            File.AppendAllText(ignorePath, (text.Length > 0 && !text.EndsWith('\n') ? Environment.NewLine : string.Empty) + "logs/bus/" + Environment.NewLine);
        Run(workspace, ["rm", "-r", "--cached", "--ignore-unmatch", "--", "logs/bus"]);
    }

    public static void CommitPlan(string workspace, RetentionPlan plan)
    {
        if (!Directory.Exists(Path.Combine(workspace, ".git"))) return;
        lock (RetentionRepositoryGate.For(workspace))
        {
            foreach (var project in plan.Tasks.Where(task => task.Project != "__workspace__")
                         .GroupBy(task => task.Project, StringComparer.OrdinalIgnoreCase))
            {
                var projectDirectory = Path.Combine(workspace, "projects", project.Key);
                var refused = Directory.EnumerateFiles(projectDirectory, "*", SearchOption.AllDirectories)
                    .Select(path => (Path: path, Relative: Path.GetRelativePath(workspace, path).Replace('\\', '/')))
                    .Where(file => ArtifactClassifier.IsCommitRefused(file.Relative, new FileInfo(file.Path).Length))
                    .Select(file => file.Relative)
                    .ToList();
                if (refused.Count > 0)
                    throw new InvalidOperationException($"Refused oversized class C files from retention evidence commit: {string.Join(", ", refused)}");
                var projectPath = $"projects/{project.Key}";
                Run(workspace, ["add", "-A", "--", projectPath]);
                if (RunCode(workspace, ["diff", "--cached", "--quiet", "--", projectPath]) == 0) continue;
                var taskCount = project.Count(task => task.ArchiveBytes > 0);
                var bytes = project.Sum(task => task.ArchiveBytes);
                Run(workspace,
                    ["-c", "user.name=agent-orchestrator", "-c", "user.email=agent-orchestrator@local", "commit", "--only", "-m", $"retention: archived {taskCount} tasks, {bytes} bytes", "--", projectPath]);
            }
            if (File.Exists(Path.Combine(workspace, ".gitignore")))
            {
                Run(workspace, ["add", "-A", "--", ".gitignore"]);
                if (RunCode(workspace, ["diff", "--cached", "--quiet", "--", ".gitignore", "logs/bus"]) != 0)
                    Run(workspace,
                        ["-c", "user.name=agent-orchestrator", "-c", "user.email=agent-orchestrator@local", "commit", "-m", "retention: untrack rotated bus logs"]);
            }
        }
    }

    private static string Run(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var (code, output, error) = RunCore(workingDirectory, arguments);
        if (code != 0) throw new InvalidOperationException($"Git {arguments[0]} failed: {(string.IsNullOrWhiteSpace(error) ? output : error).Trim()}");
        return output;
    }

    private static int RunCode(string workingDirectory, IReadOnlyList<string> arguments) => RunCore(workingDirectory, arguments).Code;

    private static (int Code, string Output, string Error) RunCore(string workingDirectory, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Git.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (process.ExitCode, stdout, stderr);
    }
}
