using System.Diagnostics;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

public record GitFileChange(string Status, string Path, int Added, int Removed);

public record GitStatusResult(
    bool IsRepo,
    string? Branch,
    int FilesChanged,
    int TotalAdded,
    int TotalRemoved,
    List<GitFileChange> Files,
    string? Error);

public record GitCommitResult(bool Success, string? Sha, string? Error);

public record GenerateMessageResult(string? Message, string? Error);

/// <summary>
/// Thin wrapper around git CLI for the per-task Git view. Operates on the
/// project's RootPath (the watched repo), not on the job folder.
/// </summary>
public class GitService
{
    private readonly ILogger<GitService> _logger;
    private readonly JobScannerService _scanner;
    private readonly IConfiguration _config;

    public GitService(ILogger<GitService> logger, JobScannerService scanner, IConfiguration config)
    {
        _logger = logger;
        _scanner = scanner;
        _config = config;
    }

    /// <summary>Resolve the repo root for a job — the watch entry's RootPath.</summary>
    public string? ResolveRepoRoot(string jobId, string? watchPath)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return null;
        var entry = _scanner.GetWatchPaths().FirstOrDefault(e => e.Name == info.ProjectName);
        return string.IsNullOrWhiteSpace(entry?.RootPath) ? null : entry.RootPath;
    }

    public GitStatusResult GetStatus(string jobId, string? watchPath)
    {
        var root = ResolveRepoRoot(jobId, watchPath);
        if (root == null)
            return new GitStatusResult(false, null, 0, 0, 0, [], "Job not found or project has no RootPath configured.");
        if (!Directory.Exists(Path.Combine(root, ".git")) && !File.Exists(Path.Combine(root, ".git")))
            return new GitStatusResult(false, null, 0, 0, 0, [], $"Not a git repository: {root}");

        var (statusOut, statusErr, statusCode) = RunGit(root, "status --porcelain=v1");
        if (statusCode != 0)
            return new GitStatusResult(true, null, 0, 0, 0, [], statusErr.Trim());

        var (branchOut, _, _) = RunGit(root, "rev-parse --abbrev-ref HEAD");
        var branch = branchOut.Trim();

        var statusByPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in statusOut.Split('\n'))
        {
            if (string.IsNullOrEmpty(line) || line.Length < 3) continue;
            var code = line[..2];
            var path = line[3..].TrimEnd('\r', '\n').Trim();
            if (path.StartsWith("\"") && path.EndsWith("\"") && path.Length >= 2)
                path = path[1..^1]; // strip git's quoting for non-ASCII
            statusByPath[path] = code;
        }

        // numstat — combine staged + unstaged so we get a useful per-file count
        // even for files only changed unstaged. Untracked files won't appear in
        // diff output; we add zero counts for those.
        var numstat = new Dictionary<string, (int Added, int Removed)>(StringComparer.Ordinal);
        foreach (var args in new[] { "diff --numstat HEAD", "diff --numstat" })
        {
            var (numOut, _, numCode) = RunGit(root, args);
            if (numCode != 0) continue;
            foreach (var line in numOut.Split('\n'))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                var parts = line.Split('\t');
                if (parts.Length < 3) continue;
                var added   = int.TryParse(parts[0], out var a) ? a : 0;
                var removed = int.TryParse(parts[1], out var r) ? r : 0;
                var path    = parts[2].Trim();
                // Take the larger of the two diffs so a staged + unstaged
                // change isn't double-counted.
                if (numstat.TryGetValue(path, out var prev))
                    numstat[path] = (Math.Max(prev.Added, added), Math.Max(prev.Removed, removed));
                else
                    numstat[path] = (added, removed);
            }
        }

        var files = statusByPath
            .Select(kv =>
            {
                var (added, removed) = numstat.TryGetValue(kv.Key, out var n) ? n : (0, 0);
                return new GitFileChange(kv.Value, kv.Key, added, removed);
            })
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new GitStatusResult(
            true,
            string.IsNullOrWhiteSpace(branch) ? null : branch,
            files.Count,
            files.Sum(f => f.Added),
            files.Sum(f => f.Removed),
            files,
            null);
    }

    public string GetDiff(string jobId, string? watchPath, string? path)
    {
        var root = ResolveRepoRoot(jobId, watchPath);
        if (root == null) return "";
        // HEAD diff catches both staged and unstaged. For untracked files we
        // fall back to showing the file body so the panel isn't empty.
        var args = string.IsNullOrWhiteSpace(path)
            ? "diff HEAD"
            : $"diff HEAD -- \"{path.Replace("\"", "\\\"")}\"";
        var (output, err, code) = RunGit(root, args);
        if (code == 0 && !string.IsNullOrWhiteSpace(output)) return output;

        if (!string.IsNullOrWhiteSpace(path))
        {
            var abs = Path.Combine(root, path);
            if (File.Exists(abs))
            {
                try { return File.ReadAllText(abs); } catch { /* best-effort */ }
            }
        }
        return string.IsNullOrWhiteSpace(output) ? err : output;
    }

    public GitCommitResult Commit(string jobId, string? watchPath, string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return new GitCommitResult(false, null, "Commit message is required.");

        var root = ResolveRepoRoot(jobId, watchPath);
        if (root == null) return new GitCommitResult(false, null, "Could not resolve repo root.");

        var (_, addErr, addCode) = RunGit(root, "add -A");
        if (addCode != 0) return new GitCommitResult(false, null, $"git add failed: {addErr.Trim()}");

        // Multi-line message via stdin avoids shell escaping landmines.
        var (_, commitErr, commitCode) = RunGit(root, "commit -F -", stdin: message);
        if (commitCode != 0)
        {
            // "nothing to commit" — surface as a soft success-with-info, not an error.
            if (commitErr.Contains("nothing to commit", StringComparison.OrdinalIgnoreCase))
                return new GitCommitResult(false, null, "Nothing to commit — working tree is clean.");
            return new GitCommitResult(false, null, commitErr.Trim());
        }

        var (sha, _, _) = RunGit(root, "rev-parse --short HEAD");
        return new GitCommitResult(true, sha.Trim(), null);
    }

    /// <summary>
    /// Sends the working-tree diff to the cheapest available Gemini model and
    /// asks for a Conventional Commit message. Only the subject + short body
    /// are returned; we strip leading code-fence noise.
    /// </summary>
    public async Task<GenerateMessageResult> GenerateCommitMessageAsync(
        string jobId, string? watchPath, CancellationToken ct = default)
    {
        var root = ResolveRepoRoot(jobId, watchPath);
        if (root == null) return new GenerateMessageResult(null, "Could not resolve repo root.");

        var (diff, _, code) = RunGit(root, "diff HEAD");
        if (code != 0 || string.IsNullOrWhiteSpace(diff))
            return new GenerateMessageResult(null, "No diff against HEAD — nothing to summarise.");

        // Bound the prompt size — Gemini Flash handles plenty but huge diffs
        // just waste latency for a commit message.
        if (diff.Length > 60_000) diff = diff[..60_000] + "\n[…truncated]";

        var prompt =
            "Write a single Conventional Commit message for the following diff. " +
            "Use one short subject line (<=72 chars), then an optional body of " +
            "1-3 short bullet points. Output ONLY the commit message — no markdown " +
            "fences, no preamble, no trailing notes.\n\n" +
            "DIFF:\n" + diff;

        var geminiPath = _config["GeminiCli:Path"] ?? "gemini";
        var model = _config["GeminiCli:CommitMsgModel"] ?? "gemini-2.5-flash-lite";

        var psi = new ProcessStartInfo
        {
            FileName = ResolveExecutable(geminiPath),
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add("-m"); psi.ArgumentList.Add(model);
        psi.ArgumentList.Add("-p"); psi.ArgumentList.Add(prompt);

        try
        {
            using var p = Process.Start(psi)!;
            var stdoutTask = p.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = p.StandardError.ReadToEndAsync(ct);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(60));
            await p.WaitForExitAsync(cts.Token);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;
            if (p.ExitCode != 0)
                return new GenerateMessageResult(null, $"gemini exited {p.ExitCode}: {stderr.Trim()}");

            var msg = SanitizeCommitMessage(stdout);
            if (string.IsNullOrWhiteSpace(msg))
                return new GenerateMessageResult(null, "gemini returned an empty message.");
            return new GenerateMessageResult(msg, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to invoke gemini for commit message");
            return new GenerateMessageResult(null, ex.Message);
        }
    }

    public bool OpenInVsCode(string jobId, string? watchPath, out string? error)
    {
        error = null;
        var root = ResolveRepoRoot(jobId, watchPath);
        if (root == null) { error = "Could not resolve repo root."; return false; }
        if (!Directory.Exists(root)) { error = $"Path does not exist: {root}"; return false; }

        var codePath = _config["VsCode:Path"] ?? "code";
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ResolveExecutable(codePath),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                WorkingDirectory = root
            };
            // -r reuses the existing window if one is open for this folder; if
            // no instance exists yet, a new one starts. That's the closest
            // we can get without an unreliable PID/window-title scan.
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add(root);
            using var p = Process.Start(psi);
            return p != null;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string SanitizeCommitMessage(string raw)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n')
            .SkipWhile(l => l.StartsWith("```"))
            .Reverse().SkipWhile(l => l.StartsWith("```") || string.IsNullOrWhiteSpace(l)).Reverse();
        return string.Join("\n", lines).Trim();
    }

    private static (string Out, string Err, int Code) RunGit(string cwd, string args, string? stdin = null)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = args,
            WorkingDirectory = cwd,
            RedirectStandardInput = stdin != null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        try
        {
            using var p = Process.Start(psi)!;
            if (stdin != null)
            {
                p.StandardInput.Write(stdin);
                p.StandardInput.Close();
            }
            var so = p.StandardOutput.ReadToEnd();
            var se = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(30_000))
            {
                try { p.Kill(true); } catch { }
                return ("", "git timed out", -1);
            }
            return (so, se, p.ExitCode);
        }
        catch (Exception ex)
        {
            return ("", ex.Message, -1);
        }
    }

    private static string ResolveExecutable(string command)
    {
        // ProcessStartInfo on Windows resolves PATH lookups for us when
        // UseShellExecute=false and the command is a bare name; we just need
        // to make sure we forward what the user configured.
        return command;
    }
}
