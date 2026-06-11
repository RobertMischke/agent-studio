using Microsoft.Extensions.Logging;

using AgentStudio.Shared;

namespace AgentStudio.Cli;

/// <summary>
/// Builds the per-CLI Working-Memory report and performs guarded deletes for the
/// Admin/CLI panel (ASS-1748 / T1c). For each CLI it enumerates the persistent
/// memory / session state the CLI keeps under its config home (user memory,
/// session / transcript stores, prompt history), enriching each with on-disk
/// size, last-write time, and a short content preview. The CLI's auth /
/// credential and base-config files are reported too, but as
/// <see cref="CliWorkingMemoryEntry.Deletable"/> = false so the panel shows them
/// as protected.
///
/// <para>
/// <b>Safety.</b> This is the only surface that deletes a CLI's accumulated
/// state, so the delete path is defensive: it rebuilds the report and only acts
/// on a path that the report itself returned as a deletable memory / session
/// entry. Auth / config entries (and anything outside the CLI's config root) are
/// refused. Deleting working memory therefore can never remove credentials.
/// </para>
/// </summary>
public sealed class CliWorkingMemoryService
{
    private readonly ILogger<CliWorkingMemoryService> _logger;
    private readonly Func<string?> _homeResolver;

    // Bounds the directory walk so a large session store (~/.claude/projects can
    // hold thousands of transcripts) cannot turn a panel load into an unbounded
    // disk scan. Size / count past the cap are reported as a lower bound.
    private const int MaxWalkFiles = 20_000;
    private const int PreviewMaxChars = 1_500;
    private const int DirPreviewChildren = 4;

    public CliWorkingMemoryService(ILogger<CliWorkingMemoryService> logger)
        : this(logger, DefaultHome) { }

    /// <summary>Test seam: inject the user-profile home so the probe is deterministic.</summary>
    internal CliWorkingMemoryService(ILogger<CliWorkingMemoryService> logger, Func<string?> homeResolver)
    {
        _logger = logger;
        _homeResolver = homeResolver;
    }

    private static string? DefaultHome() =>
        Environment.GetEnvironmentVariable("USERPROFILE") ?? Environment.GetEnvironmentVariable("HOME");

    /// <summary>
    /// Build the working-memory report for <paramref name="cliType"/>. Only
    /// states that currently exist on disk are listed, so the panel reflects the
    /// real accumulated state rather than every place one could live.
    /// </summary>
    public CliWorkingMemoryReport Describe(string? cliType)
    {
        var cli = CliTypes.Normalize(cliType);
        var root = ResolveRoot(cli);
        var entries = new List<CliWorkingMemoryEntry>();

        foreach (var d in Descriptors(cli, root))
        {
            var entry = BuildEntry(cli, d);
            if (entry != null) entries.Add(entry);
        }

        // Deletable (memory / session) first, then protected (auth / config), so
        // the panel leads with the actionable rows.
        entries = entries
            .OrderByDescending(e => e.Deletable)
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.Label, StringComparer.Ordinal)
            .ToList();

        return new CliWorkingMemoryReport
        {
            CliType = cli,
            Available = root != null && SafeDirExists(root),
            Root = root,
            CapturedAt = DateTime.UtcNow,
            Entries = entries,
        };
    }

    /// <summary>
    /// Delete one working-memory state by absolute path. Defensive: the path must
    /// match a freshly-described <b>deletable</b> entry for this CLI. Auth / config
    /// and unknown / out-of-root paths are refused. Returns the refreshed report.
    /// </summary>
    public CliWorkingMemoryDeleteResult Delete(string? cliType, string? path)
    {
        var cli = CliTypes.Normalize(cliType);
        if (string.IsNullOrWhiteSpace(path))
        {
            return new CliWorkingMemoryDeleteResult
            {
                Status = CliWorkingMemoryDeleteStatus.NotFound,
                Message = "No path supplied.",
                Report = Describe(cli),
            };
        }

        var report = Describe(cli);
        var target = NormalizePath(path);
        var match = report.Entries.FirstOrDefault(e =>
            string.Equals(NormalizePath(e.Path), target, PathComparison));

        if (match == null)
        {
            _logger.LogWarning(
                "Working-memory delete refused for {Cli}: path not a known state ({Path})", cli, path);
            return new CliWorkingMemoryDeleteResult
            {
                Status = CliWorkingMemoryDeleteStatus.NotFound,
                Message = "Path is not a known working-memory state for this CLI.",
                Report = report,
            };
        }

        if (!match.Deletable || !CliWorkingMemoryKinds.IsDeletable(match.Kind))
        {
            _logger.LogWarning(
                "Working-memory delete refused for {Cli}: {Kind} entry is protected ({Path})",
                cli, match.Kind, match.Path);
            return new CliWorkingMemoryDeleteResult
            {
                Status = CliWorkingMemoryDeleteStatus.Protected,
                Message = $"'{match.Label}' is protected ({match.Kind}) and is never deleted.",
                Report = report,
            };
        }

        try
        {
            var freed = match.SizeBytes;
            if (match.IsDirectory) Directory.Delete(match.Path, recursive: true);
            else File.Delete(match.Path);

            _logger.LogInformation(
                "Deleted {Cli} working-memory state {Kind} '{Label}' ({Bytes} bytes) at {Path}",
                cli, match.Kind, match.Label, freed, match.Path);

            return new CliWorkingMemoryDeleteResult
            {
                Status = CliWorkingMemoryDeleteStatus.Deleted,
                Message = $"Deleted '{match.Label}'.",
                FreedBytes = freed,
                Report = Describe(cli),
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete {Cli} working-memory state at {Path}", cli, match.Path);
            return new CliWorkingMemoryDeleteResult
            {
                Status = CliWorkingMemoryDeleteStatus.Error,
                Message = $"Delete failed: {ex.Message}",
                Report = Describe(cli),
            };
        }
    }

    // ── descriptors ─────────────────────────────────────────────────────────

    private readonly record struct Descriptor(
        string RelPath, string Label, string Kind, bool IsDirectory, bool Deletable, string? Detail);

    /// <summary>Resolve the CLI's config root under the user home (honouring CODEX_HOME).</summary>
    private string? ResolveRoot(string cli)
    {
        if (cli == CliTypes.Codex)
        {
            var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
            if (!string.IsNullOrWhiteSpace(codexHome)) return codexHome;
        }
        var home = _homeResolver();
        if (string.IsNullOrWhiteSpace(home)) return null;
        return cli switch
        {
            CliTypes.Claude => Path.Combine(home, ".claude"),
            CliTypes.Codex => Path.Combine(home, ".codex"),
            CliTypes.Copilot => Path.Combine(home, ".copilot"),
            CliTypes.Gemini => Path.Combine(home, ".gemini"),
            _ => null,
        };
    }

    private static readonly string PROTECTED_AUTH = "Authentication / credentials are never deleted here.";
    private static readonly string PROTECTED_CONFIG = "Base config is preserved; only memory / sessions are deletable.";

    private static IEnumerable<Descriptor> Descriptors(string cli, string? root)
    {
        if (root == null) yield break;

        switch (cli)
        {
            case CliTypes.Claude:
                yield return new("CLAUDE.md", "User memory", CliWorkingMemoryKinds.Memory, false, true, null);
                yield return new("projects", "Session store", CliWorkingMemoryKinds.Session, true, true, "Per-project conversation transcripts.");
                yield return new("todos", "Todo state", CliWorkingMemoryKinds.Session, true, true, "Per-session TodoWrite snapshots.");
                yield return new("history.jsonl", "Prompt history", CliWorkingMemoryKinds.Session, false, true, null);
                yield return new(".credentials.json", "OAuth credentials", CliWorkingMemoryKinds.Auth, false, false, PROTECTED_AUTH);
                yield return new("settings.json", "Global config", CliWorkingMemoryKinds.Config, false, false, PROTECTED_CONFIG);
                break;

            case CliTypes.Codex:
                yield return new("AGENTS.md", "User memory", CliWorkingMemoryKinds.Memory, false, true, null);
                yield return new("sessions", "Session store", CliWorkingMemoryKinds.Session, true, true, "Recorded Codex threads.");
                yield return new("history.jsonl", "Prompt history", CliWorkingMemoryKinds.Session, false, true, null);
                yield return new("auth.json", "Auth token", CliWorkingMemoryKinds.Auth, false, false, PROTECTED_AUTH);
                yield return new("config.toml", "Global config", CliWorkingMemoryKinds.Config, false, false, PROTECTED_CONFIG);
                break;

            case CliTypes.Copilot:
                yield return new("history", "Session history", CliWorkingMemoryKinds.Session, true, true, "Recorded Copilot sessions.");
                yield return new("logs", "Session logs", CliWorkingMemoryKinds.Session, true, true, null);
                yield return new("config.json", "Global config", CliWorkingMemoryKinds.Config, false, false, PROTECTED_CONFIG);
                yield return new("settings.json", "Global settings", CliWorkingMemoryKinds.Config, false, false, PROTECTED_CONFIG);
                break;

            case CliTypes.Gemini:
                yield return new("GEMINI.md", "User memory", CliWorkingMemoryKinds.Memory, false, true, null);
                yield return new("tmp", "Session checkpoints", CliWorkingMemoryKinds.Session, true, true, "Per-workspace checkpoints / chat state.");
                yield return new("settings.json", "Global config", CliWorkingMemoryKinds.Config, false, false, PROTECTED_CONFIG);
                break;
        }
    }

    // ── entry building ──────────────────────────────────────────────────────

    private CliWorkingMemoryEntry? BuildEntry(string cli, Descriptor d)
    {
        var root = ResolveRoot(cli);
        if (root == null) return null;
        var path = Path.Combine(root, d.RelPath);

        if (d.IsDirectory)
        {
            if (!SafeDirExists(path)) return null;
            var (count, bytes, lastWrite) = DirectoryStats(path);
            return new CliWorkingMemoryEntry
            {
                Id = path,
                CliType = cli,
                Kind = d.Kind,
                Label = d.Label,
                Path = path,
                IsDirectory = true,
                SizeBytes = bytes,
                ItemCount = count,
                LastModifiedUtc = lastWrite,
                Preview = DirectoryPreview(path, count),
                Deletable = d.Deletable,
                Detail = d.Detail,
            };
        }

        if (!SafeFileExists(path)) return null;
        long size = 0;
        DateTime? lastMod = null;
        try
        {
            var fi = new FileInfo(path);
            size = fi.Length;
            lastMod = fi.LastWriteTimeUtc;
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not stat working-memory file {Path}", path); }

        return new CliWorkingMemoryEntry
        {
            Id = path,
            CliType = cli,
            Kind = d.Kind,
            Label = d.Label,
            Path = path,
            IsDirectory = false,
            SizeBytes = size,
            ItemCount = null,
            LastModifiedUtc = lastMod,
            // Auth / credential bodies are never previewed - we surface that they
            // exist, never their secret contents.
            Preview = d.Kind == CliWorkingMemoryKinds.Auth ? null : FilePreview(path),
            Deletable = d.Deletable,
            Detail = d.Detail,
        };
    }

    private (int Count, long Bytes, DateTime? LastWrite) DirectoryStats(string path)
    {
        int count = 0;
        long bytes = 0;
        DateTime? last = null;
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                if (count >= MaxWalkFiles) break;
                count++;
                try
                {
                    var fi = new FileInfo(file);
                    bytes += fi.Length;
                    var w = fi.LastWriteTimeUtc;
                    if (last == null || w > last) last = w;
                }
                catch (Exception ex) { _logger.LogTrace(ex, "stat skip {File}", file); }
            }
            if (last == null) last = SafeDirLastWrite(path);
        }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not walk working-memory dir {Path}", path); }
        return (count, bytes, last);
    }

    private string DirectoryPreview(string path, int count)
    {
        try
        {
            var newest = new DirectoryInfo(path)
                .EnumerateFileSystemInfos()
                .OrderByDescending(fsi => fsi.LastWriteTimeUtc)
                .Take(DirPreviewChildren)
                .Select(fsi => fsi.Name)
                .ToList();
            var label = count >= MaxWalkFiles ? $"{count}+ items" : $"{count} item(s)";
            return newest.Count == 0
                ? label
                : $"{label} · newest: {string.Join(", ", newest)}";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not preview working-memory dir {Path}", path);
            return $"{count} item(s)";
        }
    }

    private string? FilePreview(string path)
    {
        try
        {
            using var reader = new StreamReader(path);
            var buffer = new char[PreviewMaxChars];
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0) return null;
            var text = new string(buffer, 0, read).Trim();
            if (text.Length == 0) return null;
            // Strip control chars that would mangle the panel; keep newlines/tabs.
            text = new string(text.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
            return read >= PreviewMaxChars ? text + "…" : text;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not preview working-memory file {Path}", path);
            return null;
        }
    }

    // ── path helpers ────────────────────────────────────────────────────────

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static string NormalizePath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch { return path; }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); } catch { return false; }
    }

    private static bool SafeDirExists(string path)
    {
        try { return Directory.Exists(path); } catch { return false; }
    }

    private static DateTime? SafeDirLastWrite(string path)
    {
        try { return Directory.GetLastWriteTimeUtc(path); } catch { return null; }
    }
}
