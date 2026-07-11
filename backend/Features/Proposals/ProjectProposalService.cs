using System.Diagnostics;
using System.Text.Json;

namespace AgentStudio.Proposals;

public sealed record ProjectProposal(
    string Id,
    string Generation,
    string Finding,
    string EvidenceScreenshot,
    string Proposal,
    string EstimatedEffort,
    string Severity,
    string Status,
    string? SpawnedTask,
    string RelPath,
    DateTime UpdatedAt);

public sealed record ProposalDecisionResult(ProjectProposal Proposal, string? SpawnedTask);

/// <summary>
/// Durable proposal catalogue backed by structured markdown in docs/proposals.
/// Generations are append-only; decisions update only status and spawnedTask.
/// </summary>
public sealed class ProjectProposalService
{
    private const string ProposalsRel = "docs/proposals";
    private static readonly string[] AllowedStatuses = ["proposed", "approved", "rejected", "spawned"];
    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly TaskMutationService _mutations;
    private readonly ILogger<ProjectProposalService> _logger;

    public ProjectProposalService(TaskScannerService scanner, ProjectRegistry registry,
        TaskMutationService mutations, ILogger<ProjectProposalService> logger)
    {
        _scanner = scanner;
        _registry = registry;
        _mutations = mutations;
        _logger = logger;
    }

    public IReadOnlyList<ProjectProposal>? List(string projectName)
    {
        var root = ResolveRoot(projectName);
        if (root == null) return null;
        var dir = Path.Combine(root, ProposalsRel);
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir, "*.md", SearchOption.AllDirectories)
            .Select(path => Parse(path, dir))
            .Where(p => p != null)
            .Cast<ProjectProposal>()
            .OrderByDescending(p => p.Generation, StringComparer.Ordinal)
            .ThenBy(p => SeverityRank(p.Severity))
            .ThenBy(p => p.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ProjectProposal? Get(string projectName, string id) =>
        List(projectName)?.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

    public ProposalDecisionResult? Decide(string projectName, string id, string decision)
    {
        var started = Stopwatch.GetTimestamp();
        var root = ResolveRoot(projectName);
        var proposal = Get(projectName, id);
        if (root == null || proposal == null) return null;
        if (decision is not ("approve" or "reject")) throw new ArgumentException("Decision must be approve or reject.");

        string? spawnedTask = proposal.SpawnedTask;
        var status = decision == "reject" ? "rejected" : "approved";
        if (decision == "approve" && proposal.Status != "spawned")
        {
            var watchPath = _scanner.GetWatchPaths().FirstOrDefault(entry =>
                string.Equals(entry.Name, projectName, StringComparison.OrdinalIgnoreCase))?.Path;
            if (string.IsNullOrWhiteSpace(watchPath))
                throw new InvalidOperationException("Proposal project has no task storage path.");
            var prompt = BuildImplementationPrompt(proposal);
            var createdId = _mutations.CreateJob(new CreateTaskRequest
            {
                Id = $"proposal-{proposal.Id}",
                Title = proposal.Proposal,
                WatchPath = watchPath,
                TargetState = TaskStates.Backlog,
                Agent = "codex",
                CliType = "codex",
                Mode = TaskModes.Coding,
                TaskType = TaskTypes.Feature,
                PromptMarkdown = prompt,
            });
            if (createdId == null) throw new InvalidOperationException("Implementation task could not be created.");
            spawnedTask = _scanner.ScanAllJobs()
                .FirstOrDefault(t => string.Equals(t.Id, createdId, StringComparison.OrdinalIgnoreCase))?.TaskKey
                ?? createdId;
            status = "spawned";
        }

        var updated = proposal with { Status = status, SpawnedTask = spawnedTask, UpdatedAt = DateTime.UtcNow };
        Write(root, updated);
        _logger.LogInformation(
            "project-proposal-decision project={Project} proposalId={ProposalId} decision={Decision} status={Status} spawnedTask={SpawnedTask} elapsedMs={ElapsedMs}",
            projectName, id, decision, status, spawnedTask, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return new ProposalDecisionResult(updated, spawnedTask);
    }

    private string? ResolveRoot(string projectName) => ProjectRepoResolver.ResolveForProject(projectName, _scanner, _registry);

    private static string BuildImplementationPrompt(ProjectProposal p) => $"""
        # Implement approved project proposal {p.Id}

        Source document: `docs/proposals/{p.RelPath}`

        ## Finding

        {p.Finding}

        ## Evidence

        `{p.EvidenceScreenshot}`

        ## Approved proposal

        {p.Proposal}

        Estimated effort: {p.EstimatedEffort}
        Severity: {p.Severity}

        Implement the approved change, add proportionate regression coverage, and preserve the proposal document as history.
        """;

    private static void Write(string root, ProjectProposal p)
    {
        var full = Path.GetFullPath(Path.Combine(root, ProposalsRel, p.RelPath));
        var dir = Path.GetFullPath(Path.Combine(root, ProposalsRel));
        if (!full.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Proposal path escaped docs/proposals.");
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, Render(p));
    }

    internal static string Render(ProjectProposal p) => $"""
        ---
        id: {Quote(p.Id)}
        generation: {Quote(p.Generation)}
        finding: {Quote(p.Finding)}
        evidenceScreenshot: {Quote(p.EvidenceScreenshot)}
        proposal: {Quote(p.Proposal)}
        estimatedEffort: {Quote(p.EstimatedEffort)}
        severity: {Quote(p.Severity)}
        status: {Quote(p.Status)}
        spawnedTask: {(p.SpawnedTask == null ? "null" : Quote(p.SpawnedTask))}
        ---

        # {p.Proposal}

        ## Finding

        {p.Finding}

        ## Evidence

        ![Evidence screenshot]({EvidenceLink(p)})

        ## Proposal

        {p.Proposal}

        Estimated effort: **{p.EstimatedEffort}**  
        Severity: **{p.Severity}**
        """;

    private static string EvidenceLink(ProjectProposal p)
    {
        var documentDir = Path.GetDirectoryName(p.RelPath.Replace('/', Path.DirectorySeparatorChar)) ?? "";
        return Path.GetRelativePath(documentDir, p.EvidenceScreenshot.Replace('/', Path.DirectorySeparatorChar)).Replace('\\', '/');
    }

    private static string Quote(string value) => JsonSerializer.Serialize(value);

    private static ProjectProposal? Parse(string path, string root)
    {
        try
        {
            var text = File.ReadAllText(path);
            if (!text.StartsWith("---", StringComparison.Ordinal)) return null;
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end < 0) return null;
            var values = text[3..end].Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => (line, colon: line.IndexOf(':')))
                .Where(x => x.colon > 0)
                .ToDictionary(x => x.line[..x.colon].Trim(), x => ParseValue(x.line[(x.colon + 1)..].Trim()), StringComparer.OrdinalIgnoreCase);
            string V(string key) => values.TryGetValue(key, out var value) ? value ?? "" : "";
            var status = V("status");
            if (!AllowedStatuses.Contains(status, StringComparer.OrdinalIgnoreCase)) return null;
            var rel = Path.GetRelativePath(root, path).Replace('\\', '/');
            return new ProjectProposal(V("id"), V("generation"), V("finding"), V("evidenceScreenshot"),
                V("proposal"), V("estimatedEffort"), V("severity"), status,
                values.TryGetValue("spawnedTask", out var task) ? task : null, rel, File.GetLastWriteTimeUtc(path));
        }
        catch { return null; }
    }

    private static string? ParseValue(string value)
    {
        if (value == "null") return null;
        return value.StartsWith('"') ? JsonSerializer.Deserialize<string>(value) : value;
    }

    private static int SeverityRank(string severity) => severity.ToLowerInvariant() switch
    {
        "critical" => 0,
        "medium" => 1,
        _ => 2,
    };
}
