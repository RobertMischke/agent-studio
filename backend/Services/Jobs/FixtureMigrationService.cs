using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// Result row for a single job evaluated by <see cref="FixtureMigrationService.Scan"/>.
/// </summary>
public record FixtureMigrationRow
{
    public string JobId { get; init; } = "";
    public string Title { get; init; } = "";
    public string State { get; init; } = "";
    public string ProjectName { get; init; } = "";
    public string FolderPath { get; init; } = "";
    /// <summary>True when the job already carries <c>fixture: true</c>.</summary>
    public bool AlreadyMarked { get; init; }
    /// <summary>True when the heuristic says this folder is fixture-shaped.</summary>
    public bool MatchesHeuristic { get; init; }
    /// <summary>True when this row would be touched by an apply pass.</summary>
    public bool WouldMark { get; init; }
    /// <summary>True after a successful write. Only set when <c>apply=true</c>.</summary>
    public bool Marked { get; init; }
}

public record FixtureMigrationReport
{
    /// <summary>True when the scan ran in apply mode (writes happened).</summary>
    public bool Applied { get; init; }
    public int TotalScanned { get; init; }
    public int AlreadyMarked { get; init; }
    public int MatchedHeuristic { get; init; }
    public int WouldMark { get; init; }
    public int Marked { get; init; }
    public List<FixtureMigrationRow> Rows { get; init; } = [];
}

/// <summary>
/// One-shot migration helper that retrofits the <c>fixture: true</c>
/// marker onto legacy E2E-fixture-shaped folders. Dry-run by default;
/// pass <c>apply=true</c> to actually rewrite the job.json files.
/// Idempotent: running it again on a cleaned workspace is a no-op.
/// </summary>
public class FixtureMigrationService
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<FixtureMigrationService> _logger;

    public FixtureMigrationService(TaskScannerService scanner, ILogger<FixtureMigrationService> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    public FixtureMigrationReport Scan(bool apply)
    {
        var jobs = _scanner.ScanAllJobs();
        var rows = new List<FixtureMigrationRow>(jobs.Count);
        var applied = 0;

        foreach (var job in jobs)
        {
            var matches = FixtureHeuristics.IsLikelyFixture(job);
            var wouldMark = matches && !job.Fixture;
            var marked = false;
            if (apply && wouldMark)
            {
                TaskJsonFile.UpdateField(job.FolderPath, "fixture", true, _logger);
                marked = true;
                applied++;
                _logger.LogInformation(
                    "fixture-migration: marked job {JobId} ({Title}) in {State} as fixture",
                    job.Id, job.Title, job.State);
            }

            if (matches || job.Fixture)
            {
                rows.Add(new FixtureMigrationRow
                {
                    JobId = job.Id,
                    Title = job.Title,
                    State = job.State,
                    ProjectName = job.ProjectName,
                    FolderPath = job.FolderPath,
                    AlreadyMarked = job.Fixture,
                    MatchesHeuristic = matches,
                    WouldMark = wouldMark,
                    Marked = marked
                });
            }
        }

        return new FixtureMigrationReport
        {
            Applied = apply,
            TotalScanned = jobs.Count,
            AlreadyMarked = rows.Count(r => r.AlreadyMarked),
            MatchedHeuristic = rows.Count(r => r.MatchesHeuristic),
            WouldMark = rows.Count(r => r.WouldMark),
            Marked = applied,
            Rows = rows
        };
    }
}
