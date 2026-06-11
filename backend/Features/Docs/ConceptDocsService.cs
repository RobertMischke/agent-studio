using System.Text.RegularExpressions;

namespace AgentStudio.Docs;

/// <summary>
/// Read-only loader for the in-product concept-docs at
/// <c>docs/in-app-help/lane-guides/</c>. Each markdown file in that folder is one
/// topic; the file basename (without extension) is its topic id and the
/// addressing key for <c>GET /api/concept-docs/{topic}</c>.
///
/// The committed markdown is the single source of truth: the FE never
/// duplicates the prose. This service does no rendering, only a strict
/// path-safety check + a small parse to split the title (first H1) from
/// the body.
/// </summary>
public class ConceptDocsService
{
    private static readonly Regex TopicSafe = new("^[a-z0-9][a-z0-9-]*$", RegexOptions.Compiled);

    private readonly ILogger<ConceptDocsService> _logger;

    public ConceptDocsService(ILogger<ConceptDocsService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Resolves a topic id (e.g. <c>lane-4-auto-review</c>) to the parsed
    /// concept-doc record, or <c>null</c> when the topic is malformed,
    /// the file does not exist, or path-traversal is detected.
    /// </summary>
    public ConceptDoc? Get(string topic)
    {
        if (string.IsNullOrWhiteSpace(topic) || !TopicSafe.IsMatch(topic))
        {
            return null;
        }

        var docsRoot = ResolveConceptDocsRoot();
        if (docsRoot == null) return null;

        var fullPath = Path.GetFullPath(Path.Combine(docsRoot, topic + ".md"));
        if (!fullPath.StartsWith(docsRoot, StringComparison.OrdinalIgnoreCase)) return null;
        if (!File.Exists(fullPath)) return null;

        string text;
        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Failed to read concept-doc {Topic} at {Path}", topic, fullPath);
            return null;
        }

        var (title, body) = SplitTitleAndBody(text, topic);
        return new ConceptDoc(topic, title, body);
    }

    private static (string title, string body) SplitTitleAndBody(string raw, string fallbackTitle)
    {
        var lines = raw.Replace("\r\n", "\n").Split('\n');
        var title = fallbackTitle;
        var startIdx = 0;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i].TrimStart();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith("# "))
            {
                title = line[2..].Trim();
                startIdx = i + 1;
            }
            break;
        }
        // Drop one leading blank line after the title so the body starts clean.
        while (startIdx < lines.Length && string.IsNullOrWhiteSpace(lines[startIdx]))
        {
            startIdx++;
        }
        var body = string.Join("\n", lines, startIdx, lines.Length - startIdx).TrimEnd();
        return (title, body);
    }

    /// <summary>
    /// Walks up from <c>AppContext.BaseDirectory</c> looking for
    /// <c>docs/in-app-help/lane-guides/</c>. Mirrors the resolver pattern used for
    /// <c>AgentRules:CorePath</c> so dev / stable / test layouts all
    /// find the same folder without configuration.
    /// </summary>
    private static string? ResolveConceptDocsRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "in-app-help", "lane-guides");
            if (Directory.Exists(candidate))
            {
                return Path.GetFullPath(candidate) + Path.DirectorySeparatorChar;
            }
            dir = dir.Parent;
        }
        return null;
    }
}

public record ConceptDoc(string Topic, string Title, string Body);
