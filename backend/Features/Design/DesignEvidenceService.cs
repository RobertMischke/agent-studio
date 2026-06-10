using System.Text.RegularExpressions;

namespace AgentStudio.Design;

/// <summary>
/// Read + accept surface for the project UX/UI panel (slice 6 of the
/// quality-system mockup, docs/mockups/quality-system/). Each watched
/// project has an optional <c>design/</c> folder under its workspace
/// directory:
/// <list type="bullet">
///   <item><c>design/brief.md</c> - Markdown brief, frontmatter contains status + last-updated.</item>
///   <item><c>design/references/&lt;slug&gt;.md</c> - kind=accepted|rejected|external + optional sibling image.</item>
///   <item><c>design/council/YYYY-MM-DD-&lt;slug&gt;.md</c> - council critique notes.</item>
///   <item><c>design/loop.md</c> - current iteration status, last action, last council date.</item>
/// </list>
///
/// Action-driven principle (README "Action-Driven Principle"): this service
/// only enumerates and parses what is on disk. The four action buttons in
/// the panel are separate POST endpoints that queue normal CLI jobs.
/// </summary>
public sealed class DesignEvidenceService
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<DesignEvidenceService> _logger;

    private static readonly Regex DateInFileNameRegex = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})\b",
        RegexOptions.Compiled);

    public DesignEvidenceService(TaskScannerService scanner, ILogger<DesignEvidenceService> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    /// <summary>
    /// Returns the design folder root for the project, or null when the
    /// project name is unknown. The folder is not required to exist.
    /// </summary>
    public string? ResolveDesignDir(string projectName)
    {
        var entry = FindProject(projectName);
        if (entry is null) return null;
        if (string.IsNullOrWhiteSpace(entry.Path)) return null;
        return Path.Combine(entry.Path, "design");
    }

    /// <summary>
    /// Builds the four-card top-row payload: status, references count,
    /// screenshot accepted/rejected counts, council notes count. Reads
    /// brief.md / loop.md for the status string and falls back to a
    /// neutral "no iteration" when neither file is present.
    /// </summary>
    public DesignOverview GetOverview(string projectName)
    {
        var designDir = ResolveDesignDir(projectName);
        if (designDir is null)
        {
            return new DesignOverview(
                projectName, string.Empty, false, "unknown",
                null, null, 0, 0, 0, 0, 0, 0, false, null);
        }
        if (!Directory.Exists(designDir))
        {
            return new DesignOverview(
                projectName, designDir, false, "no iteration",
                "No design/ folder yet", null, 0, 0, 0, 0, 0, 0, false, null);
        }

        var (briefExists, briefSummary, briefStatus) = ReadBrief(designDir);
        var (loopStatus, loopDetail, lastReview) = ReadLoop(designDir);
        var status = !string.IsNullOrWhiteSpace(loopStatus) ? loopStatus
            : (!string.IsNullOrWhiteSpace(briefStatus) ? briefStatus : "no iteration");

        var refs = ListReferencesInternal(designDir);
        var council = ListCouncilNotesInternal(designDir);

        var accepted = 0;
        var rejected = 0;
        var external = 0;
        foreach (var r in refs)
        {
            switch (r.Kind)
            {
                case "accepted": accepted++; break;
                case "rejected": rejected++; break;
                case "external": external++; break;
            }
        }

        var open = 0;
        var acceptedNotes = 0;
        foreach (var n in council)
        {
            if (string.IsNullOrWhiteSpace(n.AcceptedAt)) open++;
            else acceptedNotes++;
        }

        return new DesignOverview(
            ProjectName: projectName,
            DesignDir: designDir,
            Exists: true,
            Status: status,
            StatusDetail: loopDetail ?? briefSummary,
            LastReviewDate: lastReview,
            ReferencesCount: refs.Count,
            ScreenshotsAcceptedCount: accepted,
            ScreenshotsRejectedCount: rejected,
            ExternalCount: external,
            CouncilOpenCount: open,
            CouncilAcceptedCount: acceptedNotes,
            BriefExists: briefExists,
            BriefSummary: briefSummary);
    }

    public DesignReferencesResponse ListReferences(string projectName)
    {
        var designDir = ResolveDesignDir(projectName);
        if (designDir is null)
            return new DesignReferencesResponse(projectName, string.Empty, false, Array.Empty<DesignReferenceItem>());
        var refsDir = Path.Combine(designDir, "references");
        if (!Directory.Exists(refsDir))
            return new DesignReferencesResponse(projectName, refsDir, false, Array.Empty<DesignReferenceItem>());
        var list = ListReferencesInternal(designDir);
        return new DesignReferencesResponse(projectName, refsDir, true, list);
    }

    public DesignCouncilResponse ListCouncilNotes(string projectName)
    {
        var designDir = ResolveDesignDir(projectName);
        if (designDir is null)
            return new DesignCouncilResponse(projectName, string.Empty, false, Array.Empty<DesignCouncilNote>());
        var councilDir = Path.Combine(designDir, "council");
        if (!Directory.Exists(councilDir))
            return new DesignCouncilResponse(projectName, councilDir, false, Array.Empty<DesignCouncilNote>());
        var list = ListCouncilNotesInternal(designDir);
        return new DesignCouncilResponse(projectName, councilDir, true, list);
    }

    /// <summary>
    /// Reads one council note in raw form for the "unstructured report"
    /// fallback. Returns null when the project / file is unknown or when
    /// the path escapes the council folder.
    /// </summary>
    public string? ReadCouncilNote(string projectName, string fileName)
    {
        if (!IsBareMarkdownName(fileName)) return null;
        var designDir = ResolveDesignDir(projectName);
        if (designDir is null) return null;
        var councilDir = Path.Combine(designDir, "council");
        var full = Path.GetFullPath(Path.Combine(councilDir, fileName));
        var root = Path.GetFullPath(councilDir);
        if (!IsUnderRoot(full, root)) return null;
        if (!File.Exists(full)) return null;
        return File.ReadAllText(full);
    }

    public string? ReadReference(string projectName, string fileName)
    {
        if (!IsBareMarkdownName(fileName)) return null;
        var designDir = ResolveDesignDir(projectName);
        if (designDir is null) return null;
        var refsDir = Path.Combine(designDir, "references");
        var full = Path.GetFullPath(Path.Combine(refsDir, fileName));
        var root = Path.GetFullPath(refsDir);
        if (!IsUnderRoot(full, root)) return null;
        if (!File.Exists(full)) return null;
        return File.ReadAllText(full);
    }

    /// <summary>
    /// Stamps an <c>acceptedAt</c> field into the council note's
    /// frontmatter (creating frontmatter if absent) and returns the new
    /// timestamp. Returns null when the project / file is unknown.
    /// </summary>
    public AcceptCouncilNoteResponse? AcceptCouncilNote(string projectName, string fileName)
    {
        if (!IsBareMarkdownName(fileName)) return null;
        var designDir = ResolveDesignDir(projectName);
        if (designDir is null) return null;
        var councilDir = Path.Combine(designDir, "council");
        var full = Path.GetFullPath(Path.Combine(councilDir, fileName));
        var root = Path.GetFullPath(councilDir);
        if (!IsUnderRoot(full, root)) return null;
        if (!File.Exists(full)) return null;

        var stamp = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        string text;
        try { text = File.ReadAllText(full); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read council note {File} for accept stamp", full);
            return null;
        }
        var updated = StampAcceptedAt(text, stamp);
        try { File.WriteAllText(full, updated); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to write accepted-at into council note {File}", full);
            return null;
        }
        var parse = DesignEvidenceParser.Parse(updated);
        return new AcceptCouncilNoteResponse(fileName, stamp, parse.ParseOk);
    }

    private List<DesignReferenceItem> ListReferencesInternal(string designDir)
    {
        var refsDir = Path.Combine(designDir, "references");
        var items = new List<DesignReferenceItem>();
        if (!Directory.Exists(refsDir)) return items;
        foreach (var path in Directory.EnumerateFiles(refsDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            try { items.Add(BuildReferenceItem(designDir, refsDir, path)); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read design reference {File}", path);
                var fi = new FileInfo(path);
                items.Add(new DesignReferenceItem(
                    FileName: fi.Name,
                    RelPath: Path.GetRelativePath(designDir, path).Replace('\\', '/'),
                    Kind: "external",
                    Title: null,
                    Summary: null,
                    ScreenshotRelPath: null,
                    UpdatedAt: fi.Exists ? fi.LastWriteTimeUtc : DateTime.UtcNow,
                    ParseOk: false,
                    ParseError: $"unreadable: {ex.GetType().Name}"));
            }
        }
        items.Sort((a, b) => DateTime.Compare(b.UpdatedAt, a.UpdatedAt));
        return items;
    }

    private List<DesignCouncilNote> ListCouncilNotesInternal(string designDir)
    {
        var councilDir = Path.Combine(designDir, "council");
        var items = new List<DesignCouncilNote>();
        if (!Directory.Exists(councilDir)) return items;
        foreach (var path in Directory.EnumerateFiles(councilDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            try { items.Add(BuildCouncilNote(designDir, councilDir, path)); }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read council note {File}", path);
                var fi = new FileInfo(path);
                items.Add(new DesignCouncilNote(
                    FileName: fi.Name,
                    RelPath: Path.GetRelativePath(designDir, path).Replace('\\', '/'),
                    Category: null,
                    Title: null,
                    Summary: null,
                    NoteDate: ExtractDateFromFileName(fi.Name),
                    AcceptedAt: null,
                    UpdatedAt: fi.Exists ? fi.LastWriteTimeUtc : DateTime.UtcNow,
                    ParseOk: false,
                    ParseError: $"unreadable: {ex.GetType().Name}"));
            }
        }
        // Newest first by note date when available, else mtime.
        items.Sort((a, b) =>
        {
            var keyA = a.NoteDate ?? string.Empty;
            var keyB = b.NoteDate ?? string.Empty;
            var cmp = string.CompareOrdinal(keyB, keyA);
            if (cmp != 0) return cmp;
            return DateTime.Compare(b.UpdatedAt, a.UpdatedAt);
        });
        return items;
    }

    private DesignReferenceItem BuildReferenceItem(string designDir, string refsDir, string filePath)
    {
        var fi = new FileInfo(filePath);
        var text = File.ReadAllText(filePath);
        var parse = DesignEvidenceParser.Parse(text);
        var rawKind = DesignEvidenceParser.GetString(parse.Fields, "kind")
            ?? DesignEvidenceParser.GetString(parse.Fields, "category");
        var kind = DesignEvidenceParser.NormaliseKind(rawKind);
        var screenshot = DesignEvidenceParser.GetString(parse.Fields, "screenshot")
            ?? DesignEvidenceParser.GetString(parse.Fields, "image")
            ?? GuessSiblingImage(refsDir, fi.Name);
        var screenshotRel = ResolveDesignRelPath(designDir, refsDir, screenshot);
        return new DesignReferenceItem(
            FileName: fi.Name,
            RelPath: Path.GetRelativePath(designDir, filePath).Replace('\\', '/'),
            Kind: kind,
            Title: DesignEvidenceParser.GetString(parse.Fields, "title"),
            Summary: DesignEvidenceParser.GetString(parse.Fields, "summary"),
            ScreenshotRelPath: screenshotRel,
            UpdatedAt: fi.LastWriteTimeUtc,
            ParseOk: parse.ParseOk,
            ParseError: parse.ParseError);
    }

    private DesignCouncilNote BuildCouncilNote(string designDir, string councilDir, string filePath)
    {
        var fi = new FileInfo(filePath);
        var text = File.ReadAllText(filePath);
        var parse = DesignEvidenceParser.Parse(text);
        var dateFromName = ExtractDateFromFileName(fi.Name);
        return new DesignCouncilNote(
            FileName: fi.Name,
            RelPath: Path.GetRelativePath(designDir, filePath).Replace('\\', '/'),
            Category: DesignEvidenceParser.GetString(parse.Fields, "category")
                ?? DesignEvidenceParser.GetString(parse.Fields, "kind")
                ?? DesignEvidenceParser.GetString(parse.Fields, "tag"),
            Title: DesignEvidenceParser.GetString(parse.Fields, "title")
                ?? DesignEvidenceParser.GetString(parse.Fields, "role"),
            Summary: DesignEvidenceParser.GetString(parse.Fields, "summary")
                ?? DesignEvidenceParser.GetString(parse.Fields, "note"),
            NoteDate: DesignEvidenceParser.GetString(parse.Fields, "date")
                ?? DesignEvidenceParser.GetString(parse.Fields, "noteDate")
                ?? dateFromName,
            AcceptedAt: DesignEvidenceParser.GetString(parse.Fields, "acceptedAt")
                ?? DesignEvidenceParser.GetString(parse.Fields, "accepted_at"),
            UpdatedAt: fi.LastWriteTimeUtc,
            ParseOk: parse.ParseOk,
            ParseError: parse.ParseError);
    }

    private (bool exists, string? summary, string? status) ReadBrief(string designDir)
    {
        var briefPath = Path.Combine(designDir, "brief.md");
        if (!File.Exists(briefPath)) return (false, null, null);
        try
        {
            var text = File.ReadAllText(briefPath);
            var parse = DesignEvidenceParser.Parse(text);
            return (
                true,
                DesignEvidenceParser.GetString(parse.Fields, "summary"),
                DesignEvidenceParser.GetString(parse.Fields, "status"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read brief.md {File}", briefPath);
            return (true, null, null);
        }
    }

    private (string? status, string? detail, string? lastReview) ReadLoop(string designDir)
    {
        var loopPath = Path.Combine(designDir, "loop.md");
        if (!File.Exists(loopPath)) return (null, null, null);
        try
        {
            var text = File.ReadAllText(loopPath);
            var parse = DesignEvidenceParser.Parse(text);
            return (
                DesignEvidenceParser.GetString(parse.Fields, "status"),
                DesignEvidenceParser.GetString(parse.Fields, "lastAction")
                    ?? DesignEvidenceParser.GetString(parse.Fields, "last_action"),
                DesignEvidenceParser.GetString(parse.Fields, "lastCouncil")
                    ?? DesignEvidenceParser.GetString(parse.Fields, "last_council"));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read loop.md {File}", loopPath);
            return (null, null, null);
        }
    }

    private static string? GuessSiblingImage(string refsDir, string mdFileName)
    {
        var stem = Path.GetFileNameWithoutExtension(mdFileName);
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".webp", ".gif" })
        {
            var candidate = Path.Combine(refsDir, stem + ext);
            if (File.Exists(candidate)) return Path.GetFileName(candidate);
        }
        return null;
    }

    /// <summary>
    /// Resolves a frontmatter-supplied screenshot path to a path relative
    /// to the project's <c>design/</c> folder. Bare file names are
    /// resolved against the references folder.
    /// </summary>
    private static string? ResolveDesignRelPath(string designDir, string refsDir, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var trimmed = raw.Replace('\\', '/').Trim('/');
        // If frontmatter already gave a design-relative path, keep it.
        if (trimmed.StartsWith("references/", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("council/", StringComparison.OrdinalIgnoreCase))
            return trimmed;
        // Bare name: assume sibling of the .md file.
        var sibling = Path.Combine(refsDir, trimmed);
        if (File.Exists(sibling))
        {
            return Path.GetRelativePath(designDir, sibling).Replace('\\', '/');
        }
        return trimmed;
    }

    private static string ExtractDateFromFileName(string fileName)
    {
        var m = DateInFileNameRegex.Match(fileName);
        return m.Success ? m.Groups["date"].Value : string.Empty;
    }

    private static bool IsBareMarkdownName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..", StringComparison.Ordinal))
            return false;
        return name.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUnderRoot(string full, string root)
    {
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase)) return true;
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Stamps <c>acceptedAt: &lt;stamp&gt;</c> into the YAML frontmatter
    /// of the note, creating frontmatter if absent. Idempotent: replaces
    /// an existing <c>acceptedAt</c> line in place.
    /// </summary>
    internal static string StampAcceptedAt(string original, string stamp)
    {
        // No frontmatter -> wrap the body.
        if (!original.StartsWith("---", StringComparison.Ordinal))
        {
            return $"---\nacceptedAt: {stamp}\n---\n\n{original}";
        }
        var lines = original.Split('\n').ToList();
        // Locate the closing --- of frontmatter.
        var closeIdx = -1;
        for (int i = 1; i < lines.Count; i++)
        {
            if (lines[i].TrimEnd('\r').Trim() == "---") { closeIdx = i; break; }
        }
        if (closeIdx < 0)
        {
            // Malformed frontmatter; prepend a fresh block.
            return $"---\nacceptedAt: {stamp}\n---\n\n{original}";
        }
        // Replace existing acceptedAt line if present.
        var replaced = false;
        for (int i = 1; i < closeIdx; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.StartsWith("acceptedAt:", StringComparison.OrdinalIgnoreCase)
                || trimmed.StartsWith("accepted_at:", StringComparison.OrdinalIgnoreCase))
            {
                lines[i] = $"acceptedAt: {stamp}";
                replaced = true;
                break;
            }
        }
        if (!replaced)
        {
            lines.Insert(closeIdx, $"acceptedAt: {stamp}");
        }
        return string.Join('\n', lines);
    }

    private WatchPathEntry? FindProject(string projectName) =>
        _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
}
