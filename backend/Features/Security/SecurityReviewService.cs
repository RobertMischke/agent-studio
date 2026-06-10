using System.Text.RegularExpressions;

namespace AgentStudio.Security;

/// <summary>
/// Read surface for the project Security panel (slice 1 of the
/// quality-system mockup at docs/mockups/quality-system/). Each watched
/// project has an optional <c>security/</c> folder under its workspace
/// directory: <c>baseline.md</c> sits at the root, review files live in
/// <c>reviews/</c>. Both files are Markdown; structured fields are parsed
/// by <see cref="SecurityReviewParser"/>.
///
/// Action-driven principle (README "Action-Driven Principle"): this
/// service does no analysis on read; it only enumerates and parses what is
/// already on disk. The "run security audit" button is a separate code
/// path that creates a queued job.
/// </summary>
public sealed class SecurityReviewService
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<SecurityReviewService> _logger;

    private static readonly Regex DateInFileNameRegex = new(
        @"^(?<date>\d{4}-\d{2}-\d{2})\b",
        RegexOptions.Compiled);

    public SecurityReviewService(TaskScannerService scanner, ILogger<SecurityReviewService> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    /// <summary>
    /// Returns the security folder root for the project, or null when the
    /// project name is unknown. Folder is not required to exist; callers
    /// surface an empty state when it doesn't.
    /// </summary>
    public string? ResolveSecurityDir(string projectName)
    {
        var entry = FindProject(projectName);
        if (entry is null) return null;
        if (string.IsNullOrWhiteSpace(entry.Path)) return null;
        return Path.Combine(entry.Path, "security");
    }

    /// <summary>
    /// Enumerates review files under <c>security/reviews/</c>, newest first.
    /// Sort order: review date from the structured block when present,
    /// falling back to the leading <c>YYYY-MM-DD</c> in the file name, and
    /// finally to file mtime when neither is parseable.
    /// </summary>
    public SecurityReviewListResponse ListReviews(string projectName)
    {
        var secDir = ResolveSecurityDir(projectName);
        if (secDir is null)
            return new SecurityReviewListResponse(projectName, string.Empty, false, Array.Empty<SecurityReviewSummary>());

        var reviewsDir = Path.Combine(secDir, "reviews");
        if (!Directory.Exists(reviewsDir))
            return new SecurityReviewListResponse(projectName, reviewsDir, false, Array.Empty<SecurityReviewSummary>());

        var entries = new List<SecurityReviewSummary>();
        foreach (var path in Directory.EnumerateFiles(reviewsDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            try
            {
                entries.Add(BuildSummary(reviewsDir, path));
            }
            catch (Exception ex)
            {
                // Don't fail the whole list because one file is unreadable
                // (locked, busy, encoding glitch). Surface a parse-error
                // entry so the UI can still link the file for a human read.
                _logger.LogWarning(ex, "Failed to read security review file {File}", path);
                var fi = new FileInfo(path);
                entries.Add(new SecurityReviewSummary(
                    FileName: fi.Name,
                    RelPath: Path.GetRelativePath(reviewsDir, path).Replace('\\', '/'),
                    UpdatedAt: fi.Exists ? fi.LastWriteTimeUtc : DateTime.UtcNow,
                    ReviewDate: ExtractDateFromFileName(fi.Name),
                    Verdict: null,
                    Severity: null,
                    OpenFindings: null,
                    Severities: null,
                    Title: null,
                    Summary: null,
                    ParseOk: false,
                    ParseError: $"unreadable: {ex.GetType().Name}"));
            }
        }

        // Newest first: structured ReviewDate string sorts lexicographically
        // when ISO-formatted; fall back to mtime for ties or null dates.
        entries.Sort((a, b) =>
        {
            var keyA = a.ReviewDate ?? string.Empty;
            var keyB = b.ReviewDate ?? string.Empty;
            var cmp = string.CompareOrdinal(keyB, keyA);
            if (cmp != 0) return cmp;
            return DateTime.Compare(b.UpdatedAt, a.UpdatedAt);
        });

        return new SecurityReviewListResponse(projectName, reviewsDir, true, entries);
    }

    /// <summary>
    /// Reads <c>security/baseline.md</c> for the project. Returns a record
    /// even when the file is missing so the UI can render "no baseline yet"
    /// without a 404; <see cref="SecurityBaselineResponse.Exists"/> tells
    /// the two cases apart.
    /// </summary>
    public SecurityBaselineResponse GetBaseline(string projectName)
    {
        var secDir = ResolveSecurityDir(projectName);
        if (secDir is null)
        {
            return new SecurityBaselineResponse(
                ProjectName: projectName, FilePath: string.Empty, Exists: false,
                Status: null, LastVerified: null, DefinitionRef: null,
                SeverityThresholds: null, Summary: null,
                ParseOk: false, ParseError: null, Markdown: null);
        }

        var baselinePath = Path.Combine(secDir, "baseline.md");
        if (!File.Exists(baselinePath))
        {
            return new SecurityBaselineResponse(
                ProjectName: projectName, FilePath: baselinePath, Exists: false,
                Status: null, LastVerified: null, DefinitionRef: null,
                SeverityThresholds: null, Summary: null,
                ParseOk: false, ParseError: null, Markdown: null);
        }

        string text;
        try
        {
            text = File.ReadAllText(baselinePath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read security baseline {File}", baselinePath);
            return new SecurityBaselineResponse(
                ProjectName: projectName, FilePath: baselinePath, Exists: true,
                Status: null, LastVerified: null, DefinitionRef: null,
                SeverityThresholds: null, Summary: null,
                ParseOk: false, ParseError: $"unreadable: {ex.GetType().Name}", Markdown: null);
        }

        var parse = SecurityReviewParser.Parse(text);
        return new SecurityBaselineResponse(
            ProjectName: projectName,
            FilePath: baselinePath,
            Exists: true,
            Status: SecurityReviewParser.GetString(parse.Fields, "status")
                ?? SecurityReviewParser.GetString(parse.Fields, "verdict"),
            LastVerified: SecurityReviewParser.GetString(parse.Fields, "lastVerified")
                ?? SecurityReviewParser.GetString(parse.Fields, "last_verified"),
            DefinitionRef: SecurityReviewParser.GetString(parse.Fields, "definitionRef")
                ?? SecurityReviewParser.GetString(parse.Fields, "definition_ref")
                ?? SecurityReviewParser.GetString(parse.Fields, "definition"),
            SeverityThresholds: SecurityReviewParser.GetStringMap(parse.Fields, "severityThresholds")
                ?? SecurityReviewParser.GetStringMap(parse.Fields, "severity_thresholds")
                ?? SecurityReviewParser.GetStringMap(parse.Fields, "thresholds"),
            Summary: SecurityReviewParser.GetString(parse.Fields, "summary"),
            ParseOk: parse.ParseOk,
            ParseError: parse.ParseError,
            Markdown: text);
    }

    /// <summary>
    /// Reads one review file in raw form. Returns null when the project or
    /// file is unknown, or when the path escapes <c>security/reviews/</c>.
    /// </summary>
    public string? ReadReview(string projectName, string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        // Bare filenames only: this endpoint is not a generic file server.
        // Reject any path separator or relative-path component up front.
        if (fileName.Contains('/') || fileName.Contains('\\') || fileName.Contains("..", StringComparison.Ordinal))
            return null;
        if (!fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)) return null;

        var secDir = ResolveSecurityDir(projectName);
        if (secDir is null) return null;
        var reviewsDir = Path.Combine(secDir, "reviews");
        var full = Path.GetFullPath(Path.Combine(reviewsDir, fileName));
        var root = Path.GetFullPath(reviewsDir);
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
            return null;
        if (!File.Exists(full)) return null;
        return File.ReadAllText(full);
    }

    private SecurityReviewSummary BuildSummary(string reviewsDir, string filePath)
    {
        var fi = new FileInfo(filePath);
        var text = File.ReadAllText(filePath);
        var parse = SecurityReviewParser.Parse(text);
        var dateFromName = ExtractDateFromFileName(fi.Name);
        return new SecurityReviewSummary(
            FileName: fi.Name,
            RelPath: Path.GetRelativePath(reviewsDir, filePath).Replace('\\', '/'),
            UpdatedAt: fi.LastWriteTimeUtc,
            ReviewDate: SecurityReviewParser.GetString(parse.Fields, "reviewDate")
                ?? SecurityReviewParser.GetString(parse.Fields, "date")
                ?? SecurityReviewParser.GetString(parse.Fields, "review_date")
                ?? dateFromName,
            Verdict: SecurityReviewParser.GetString(parse.Fields, "verdict")
                ?? SecurityReviewParser.GetString(parse.Fields, "status"),
            Severity: SecurityReviewParser.GetString(parse.Fields, "severity"),
            OpenFindings: SecurityReviewParser.GetInt(parse.Fields, "openFindings")
                ?? SecurityReviewParser.GetInt(parse.Fields, "open_findings")
                ?? SecurityReviewParser.GetInt(parse.Fields, "findings"),
            Severities: SecurityReviewParser.GetIntMap(parse.Fields, "severities")
                ?? SecurityReviewParser.GetIntMap(parse.Fields, "findingsBySeverity"),
            Title: SecurityReviewParser.GetString(parse.Fields, "title"),
            Summary: SecurityReviewParser.GetString(parse.Fields, "summary"),
            ParseOk: parse.ParseOk,
            ParseError: parse.ParseError);
    }

    private static string? ExtractDateFromFileName(string fileName)
    {
        var m = DateInFileNameRegex.Match(fileName);
        return m.Success ? m.Groups["date"].Value : null;
    }

    private WatchPathEntry? FindProject(string projectName) =>
        _scanner.GetWatchPaths().FirstOrDefault(e =>
            string.Equals(e.Name, projectName, StringComparison.OrdinalIgnoreCase));
}
