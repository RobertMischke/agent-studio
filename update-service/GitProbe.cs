using System.Diagnostics;

namespace AgentTaskboard.UpdateService;

/// <summary>
/// Tiny git wrapper. We don't pull libgit2sharp for three commands.
/// All operations target the stable checkout configured at startup.
/// </summary>
public sealed class GitProbe
{
    private readonly string _checkoutDir;
    private readonly ILogger<GitProbe> _logger;

    public GitProbe(string checkoutDir, ILogger<GitProbe> logger)
    {
        _checkoutDir = checkoutDir;
        _logger = logger;
    }

    public string HeadShort()
    {
        return Run("rev-parse", "--short", "HEAD").Trim();
    }

    /// <summary>
    /// Fetch origin/main and return (originShort, behindBy). behindBy is the
    /// number of commits HEAD..origin/main; 0 means up to date.
    /// </summary>
    public (string Origin, int BehindBy) FetchAndCompare()
    {
        Run("fetch", "--quiet", "origin", "main");
        var origin = Run("rev-parse", "--short", "origin/main").Trim();
        var listing = Run("rev-list", "--count", "HEAD..origin/main").Trim();
        if (!int.TryParse(listing, out var behindBy)) behindBy = 0;
        return (origin, behindBy);
    }

    /// <summary>
    /// One-line summary of every commit in HEAD..origin/main, newest first.
    /// Capped at <paramref name="max"/> entries so a long backlog doesn't
    /// blow up the status JSON. Returns an empty list when behindBy=0 or
    /// when git fails.
    /// </summary>
    public IReadOnlyList<CommitInfo> PendingCommits(int max = 50)
    {
        // Format: <sha>\t<subject>\t<author>\t<iso-date>
        // The unit-separator chars are unlikely in commit messages but we
        // still split by tab; subjects with tabs are extremely rare.
        var fmt = "%h%x09%s%x09%an%x09%aI";
        var raw = Run("log", "--no-merges", $"--max-count={max}",
                      $"--pretty=format:{fmt}", "HEAD..origin/main");
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<CommitInfo>();

        var list = new List<CommitInfo>();
        foreach (var line in raw.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split('\t', 4);
            if (parts.Length < 4) continue;
            DateTime.TryParse(parts[3], null,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var when);
            list.Add(new CommitInfo(parts[0].Trim(), parts[1].Trim(), parts[2].Trim(), when));
        }
        return list;
    }

    private string Run(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _checkoutDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            _logger.LogWarning("git {Args} exited {Code}: {Err}", string.Join(' ', args), p.ExitCode, stderr.Trim());
            return "";
        }
        return stdout;
    }
}
