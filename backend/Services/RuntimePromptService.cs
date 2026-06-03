using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services;

/// <summary>
/// Loads runtime prompt templates from Markdown files and renders simple
/// <c>{{variable}}</c> placeholders. Prompt text stays editable outside the
/// codebase's control flow, while services keep owning the decisions about
/// which template is used and when.
/// </summary>
public sealed partial class RuntimePromptService
{
    public const string RunnerFreshStart = "runner-fresh-start.md";
    public const string RunnerResumeInterrupted = "runner-resume-interrupted.md";
    public const string RunnerResumeRestart = "runner-resume-restart.md";
    public const string RunnerRecoveryContinuation = "runner-recovery-continuation.md";
    public const string EpicDecomposition = "epic-decomposition.md";
    public const string SummaryProtocol = "summary-protocol.md";
    public const string CommitMessage = "commit-message.md";
    public const string ModeFramingReadOnly = "mode-framing-readonly.md";
    public const string ModeFramingWeb = "mode-framing-web.md";

    private static readonly IReadOnlyDictionary<string, string?> NoValues =
        new Dictionary<string, string?>();

    private readonly IConfiguration _configuration;
    private readonly ILogger<RuntimePromptService> _logger;
    private readonly ConcurrentDictionary<string, string> _cache = new(StringComparer.OrdinalIgnoreCase);

    public RuntimePromptService(IConfiguration configuration, ILogger<RuntimePromptService> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public string Render(string templateName, IReadOnlyDictionary<string, string?> values)
    {
        var template = Load(templateName);
        return PlaceholderRegex().Replace(template, match =>
        {
            var key = match.Groups["key"].Value.Trim();
            return values.TryGetValue(key, out var value) ? value ?? string.Empty : match.Value;
        });
    }

    /// <summary>
    /// Composes the per-mode framing block injected into runner prompts via the
    /// <c>{{mode_framing}}</c> placeholder. Read-only modes (planning / research)
    /// get the read-only block; web access (research default, or any mode the
    /// user opted into) appends the web block. Coding with web off yields an
    /// empty string so the rendered prompt is byte-identical to the pre-mode
    /// behavior. A non-empty result ends with a blank-line separator so it slots
    /// in front of the following section cleanly.
    /// </summary>
    public string RenderModeFraming(string? mode, bool allowWebAccess)
    {
        var parts = new List<string>();
        if (TaskModes.IsReadOnly(mode))
            parts.Add(Render(ModeFramingReadOnly, NoValues).Trim());
        if (allowWebAccess)
            parts.Add(Render(ModeFramingWeb, NoValues).Trim());
        if (parts.Count == 0) return string.Empty;
        return string.Join("\n\n", parts) + "\n\n";
    }

    private string Load(string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
            throw new ArgumentException("Template name is required.", nameof(templateName));

        return _cache.GetOrAdd(templateName, name =>
        {
            var path = ResolveTemplatePath(name);
            _logger.LogDebug("Loading runtime prompt template {Template} from {Path}", name, path);
            return File.ReadAllText(path);
        });
    }

    private string ResolveTemplatePath(string templateName)
    {
        foreach (var root in CandidateRoots())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var path = Path.GetFullPath(Path.Combine(root, templateName));
            if (File.Exists(path)) return path;
        }

        throw new FileNotFoundException(
            $"Runtime prompt template '{templateName}' was not found. " +
            $"Set PromptTemplates:RuntimePath or ensure prompts/runtime is copied to the output directory.");
    }

    private IEnumerable<string?> CandidateRoots()
    {
        var configured = _configuration["PromptTemplates:RuntimePath"];
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;

        yield return Path.Combine(AppContext.BaseDirectory, "prompts", "runtime");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "prompts", "runtime");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "..", "prompts", "runtime");
    }

    [GeneratedRegex(@"\{\{\s*(?<key>[A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();
}
