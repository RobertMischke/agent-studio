using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentStudio.Pipeline;

public sealed record ConceptWorkbenchReview(
    bool IsComplete,
    string? Topic,
    string? RepoRelativeDirectory,
    ConceptWorkbenchDescriptor? Descriptor,
    IReadOnlyList<string> Findings)
{
    public string Summary => IsComplete
        ? $"Concept Dossier '{Descriptor?.Title ?? Topic}' is complete and evidence-ready."
        : string.Join(" ", Findings);
}

/// <summary>
/// Deterministic concept-deliverable contract. This is the concept review:
/// structure, alternatives, recommendation, evidence, open decisions, and
/// implementation-card source data. It deliberately does not inspect builds,
/// tests, or product code.
/// </summary>
public static class ConceptWorkbenchContract
{
    public const string OperationsPrefix = "docs/operations/";
    public const string DescriptorFileName = "workbench.json";
    public const string EntryFileName = "index.html";

    private static readonly string[] RequiredSections =
        ["alternatives", "recommendation", "evidence", "open-decisions"];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Creates a valid house-style Dossier skeleton. The concept agent owns the
    /// substantive content; this helper gives the pipeline a deterministic
    /// materialization contract and makes the required two-file deliverable
    /// directly testable.
    /// </summary>
    public static string CreateScaffold(
        string repositoryRoot,
        string topic,
        string title,
        string summary,
        string sourceTaskKey)
    {
        var safeTopic = NormalizeTopic(topic);
        var target = Path.Combine(repositoryRoot, "docs", "operations", safeTopic);
        Directory.CreateDirectory(target);

        var descriptor = new ConceptWorkbenchDescriptor
        {
            Id = safeTopic,
            Title = title.Trim(),
            Summary = summary.Trim(),
            SourceTaskKeys = string.IsNullOrWhiteSpace(sourceTaskKey) ? [] : [sourceTaskKey.Trim()],
        };
        File.WriteAllText(
            Path.Combine(target, DescriptorFileName),
            JsonSerializer.Serialize(descriptor, JsonOptions) + Environment.NewLine);
        File.WriteAllText(
            Path.Combine(target, EntryFileName),
            RenderHouseStyleIndex(descriptor));
        return target;
    }

    public static ConceptWorkbenchReview ReviewChangedFiles(
        string checkoutRoot,
        IReadOnlyList<string> changedFiles)
    {
        var normalized = changedFiles
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeRepoPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var findings = new List<string>();
        if (normalized.Count == 0)
            findings.Add("The concept run produced no Dossier files.");

        var outside = normalized.Where(path =>
            !path.StartsWith(OperationsPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
        if (outside.Count > 0)
            findings.Add("Concept runs may change only docs/operations/<topic>/: " + string.Join(", ", outside));

        var topicRoots = normalized
            .Where(path => path.StartsWith(OperationsPrefix, StringComparison.OrdinalIgnoreCase))
            .Select(path =>
            {
                var remainder = path[OperationsPrefix.Length..];
                var slash = remainder.IndexOf('/');
                return slash <= 0 ? null : OperationsPrefix + remainder[..slash];
            })
            .Where(root => root is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Cast<string>()
            .ToList();
        if (topicRoots.Count != 1)
            findings.Add($"Exactly one concept Dossier is required; found {topicRoots.Count} topic directories.");

        if (findings.Count > 0)
            return new ConceptWorkbenchReview(false, null, null, null, findings);

        var rel = topicRoots[0];
        return ReviewDirectory(checkoutRoot, rel);
    }

    public static ConceptWorkbenchReview ReviewDirectory(string checkoutRoot, string repoRelativeDirectory)
    {
        var rel = NormalizeRepoPath(repoRelativeDirectory).TrimEnd('/');
        var findings = new List<string>();
        if (!rel.StartsWith(OperationsPrefix, StringComparison.OrdinalIgnoreCase)
            || rel.Split('/').Length != 3)
        {
            findings.Add("Dossier directory must be docs/operations/<topic>/.");
            return new ConceptWorkbenchReview(false, null, rel, null, findings);
        }

        var topic = rel[(OperationsPrefix.Length)..];
        var directory = Path.GetFullPath(Path.Combine(checkoutRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(checkoutRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(root, PathComparison()))
        {
            findings.Add("Dossier directory escapes the repository root.");
            return new ConceptWorkbenchReview(false, topic, rel, null, findings);
        }

        var descriptorPath = Path.Combine(directory, DescriptorFileName);
        var entryPath = Path.Combine(directory, EntryFileName);
        ConceptWorkbenchDescriptor? descriptor = null;
        if (!File.Exists(descriptorPath))
        {
            findings.Add("workbench.json is missing.");
        }
        else
        {
            try
            {
                var descriptorJson = File.ReadAllText(descriptorPath);
                descriptor = JsonSerializer.Deserialize<ConceptWorkbenchDescriptor>(
                    descriptorJson, JsonOptions);
                using var document = JsonDocument.Parse(descriptorJson);
                ValidateDescriptorShape(document.RootElement, findings);
            }
            catch (Exception ex)
            {
                findings.Add("workbench.json is invalid: " + ex.Message);
            }
        }

        if (descriptor is not null)
        {
            if (descriptor.SchemaVersion < 1) findings.Add("workbench.json schemaVersion must be at least 1.");
            if (string.IsNullOrWhiteSpace(descriptor.Id)) findings.Add("workbench.json id is required.");
            if (string.IsNullOrWhiteSpace(descriptor.Title)) findings.Add("workbench.json title is required.");
            if (string.IsNullOrWhiteSpace(descriptor.Summary)) findings.Add("workbench.json summary is required.");
            if (!string.Equals(descriptor.Entrypoint, EntryFileName, StringComparison.OrdinalIgnoreCase))
                findings.Add("workbench.json entrypoint must be index.html.");
            if (string.IsNullOrWhiteSpace(descriptor.Status)) findings.Add("workbench.json status is required.");
            if (string.IsNullOrWhiteSpace(descriptor.Phase)) findings.Add("workbench.json phase is required.");
            if (descriptor.SourceTaskKeys.Count == 0)
                findings.Add("workbench.json sourceTaskKeys must identify the source concept card.");
            for (var i = 0; i < descriptor.ImplementationTasks.Count; i++)
            {
                var item = descriptor.ImplementationTasks[i];
                if (string.IsNullOrWhiteSpace(item.Title) || string.IsNullOrWhiteSpace(item.PromptMarkdown))
                    findings.Add($"implementationTasks[{i}] requires title and promptMarkdown.");
            }
        }

        if (!File.Exists(entryPath))
        {
            findings.Add("index.html is missing.");
        }
        else
        {
            var html = File.ReadAllText(entryPath);
            if (!html.Contains("<!DOCTYPE html>", StringComparison.OrdinalIgnoreCase))
                findings.Add("index.html must be a self-contained HTML document.");
            if (!html.Contains("<style", StringComparison.OrdinalIgnoreCase))
                findings.Add("index.html must include its house-style CSS.");
            foreach (var section in RequiredSections)
            {
                if (!html.Contains($"data-concept-section=\"{section}\"", StringComparison.OrdinalIgnoreCase)
                    && !html.Contains($"data-concept-section='{section}'", StringComparison.OrdinalIgnoreCase))
                    findings.Add($"index.html is missing the {section} concept section.");
            }
        }

        return new ConceptWorkbenchReview(
            findings.Count == 0, topic, rel, descriptor, findings);
    }

    private static void ValidateDescriptorShape(JsonElement root, List<string> findings)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            findings.Add("workbench.json must be a JSON object.");
            return;
        }

        var properties = root.EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value, StringComparer.OrdinalIgnoreCase);
        string[] required =
        [
            "schemaVersion",
            "id",
            "title",
            "summary",
            "entrypoint",
            "status",
            "phase",
            "updatedAt",
            "sourceTaskKeys",
            "implementationTasks",
        ];
        foreach (var name in required)
        {
            if (!properties.ContainsKey(name))
                findings.Add($"workbench.json {name} is required.");
        }
        if (properties.TryGetValue("sourceTaskKeys", out var sources)
            && sources.ValueKind != JsonValueKind.Array)
            findings.Add("workbench.json sourceTaskKeys must be an array.");
        if (properties.TryGetValue("implementationTasks", out var tasks)
            && tasks.ValueKind != JsonValueKind.Array)
            findings.Add("workbench.json implementationTasks must be an array.");
    }

    public static string RenderHouseStyleIndex(ConceptWorkbenchDescriptor descriptor)
    {
        var title = WebUtility.HtmlEncode(descriptor.Title);
        var summary = WebUtility.HtmlEncode(descriptor.Summary);
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>{{title}} | Concept Dossier</title>
              <style>
                :root { color-scheme: light dark; --bg: #fcfcfb; --surface: #f2f1ee; --ink: #151515; --muted: #62615d; --line: #d8d6cf; }
                @media (prefers-color-scheme: dark) { :root { --bg: #191918; --surface: #242423; --ink: #f5f5f2; --muted: #b8b7af; --line: #3d3d39; } }
                * { box-sizing: border-box; } body { margin: 0; background: var(--bg); color: var(--ink); font: 16px/1.6 system-ui, sans-serif; }
                main { max-width: 72rem; margin: auto; padding: 3rem 2rem 6rem; } header { border-bottom: 1px solid var(--line); padding-bottom: 1.5rem; }
                h1 { font-size: clamp(2rem, 5vw, 3.5rem); line-height: 1.05; } h2 { margin-top: 3rem; } p { max-width: 72ch; }
                section { background: var(--surface); border: 1px solid var(--line); border-radius: .75rem; padding: 1.25rem; margin-top: 1rem; }
                .lede { color: var(--muted); font-size: 1.125rem; }
              </style>
            </head>
            <body><main>
              <header><p>Concept Dossier</p><h1>{{title}}</h1><p class="lede">{{summary}}</p></header>
              <section data-concept-section="alternatives"><h2>Alternatives</h2><p>Document the credible alternatives and tradeoffs.</p></section>
              <section data-concept-section="recommendation"><h2>Recommendation</h2><p>State the recommended default and why.</p></section>
              <section data-concept-section="evidence"><h2>Evidence</h2><p>Link observations, measurements, and constraints.</p></section>
              <section data-concept-section="open-decisions"><h2>Open decisions</h2><p>List the choices that require human sight review.</p></section>
            </main></body>
            </html>
            """;
    }

    private static string NormalizeTopic(string topic)
    {
        var normalized = new string((topic ?? "").Trim().ToLowerInvariant()
            .Select(ch => char.IsAsciiLetterOrDigit(ch) ? ch : '-').ToArray());
        while (normalized.Contains("--", StringComparison.Ordinal))
            normalized = normalized.Replace("--", "-", StringComparison.Ordinal);
        normalized = normalized.Trim('-');
        if (normalized.Length == 0) throw new ArgumentException("Concept topic is required.", nameof(topic));
        return normalized;
    }

    private static string NormalizeRepoPath(string path) =>
        path.Replace('\\', '/').TrimStart('/');

    private static StringComparison PathComparison() =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
}
