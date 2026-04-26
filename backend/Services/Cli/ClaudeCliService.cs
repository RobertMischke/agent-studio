using System.Diagnostics;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Driver for Anthropic's <c>claude</c> CLI.
/// <list type="bullet">
///   <item>First run: <c>claude -p "prompt" --name "session-name"</c> creates a named session.</item>
///   <item>Resume:    <c>claude -r "session-name" -p "prompt"</c>.</item>
///   <item>Sessions live in <c>~/.claude/projects/&lt;cwd&gt;/&lt;uuid&gt;.jsonl</c>.</item>
/// </list>
/// </summary>
public sealed class ClaudeCliService : CliExecutionServiceBase
{
    private string? _cliPathOverride;

    public ClaudeCliService(ILogger<ClaudeCliService> logger, IConfiguration configuration)
        : base(logger, configuration) { }

    public override string CliType => CliTypes.Claude;

    public override string GetCliPath()
        => _cliPathOverride
           ?? _configuration["ClaudeCli:Path"]
           ?? "claude";

    public void SetCliPath(string path)
    {
        _cliPathOverride = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
        _logger.LogInformation("Claude CLI path set to: {Path}", GetCliPath());
    }

    protected override ProcessStartInfo BuildStartInfo(
        string prompt,
        string workingDirectory,
        string? sessionName,
        bool resumeSession,
        string? model)
    {
        // claude -p <prompt> [--name <s>] [-r <s>] [--model <m>] --dangerously-skip-permissions
        // We use --dangerously-skip-permissions to mirror Copilot's --allow-all so the
        // CLI never blocks on a permission prompt during automated runs.
        var args = new List<string> { "-p", Quote(prompt) };

        if (!string.IsNullOrWhiteSpace(sessionName))
        {
            if (resumeSession) { args.Add("-r"); args.Add(Quote(sessionName)); }
            else                { args.Add("--name"); args.Add(Quote(sessionName)); }
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            args.Add("--model"); args.Add(Quote(model));
        }

        args.Add("--dangerously-skip-permissions");

        return new ProcessStartInfo
        {
            FileName = GetCliPath(),
            Arguments = string.Join(' ', args),
            WorkingDirectory = workingDirectory
        };
    }

    public override Task<CliModelCatalog> GetModelCatalogAsync(bool forceRefresh = false, CancellationToken ct = default)
    {
        // No live discovery yet — surface the well-known Claude 4.x family. The
        // user picks one, the CLI validates. Empty list also works (default).
        var models = new List<CliModelInfo>
        {
            new() { Id = "claude-opus-4-7",       Label = "Claude Opus 4.7",     Vendor = "anthropic" },
            new() { Id = "claude-sonnet-4-6",     Label = "Claude Sonnet 4.6",   Vendor = "anthropic", IsDefault = true },
            new() { Id = "claude-haiku-4-5",      Label = "Claude Haiku 4.5",    Vendor = "anthropic" }
        };
        return Task.FromResult(new CliModelCatalog
        {
            Models = models,
            Source = "hardcoded",
            FetchedAt = DateTime.UtcNow
        });
    }

    private static string Quote(string s) => $"\"{s.Replace("\"", "\\\"")}\"";
}
