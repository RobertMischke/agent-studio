using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The write half of the Sichtblick gate (AGT-2375). The decision must land in
/// the Workbench's own <c>workbench.json</c> - that file, not a
/// <c>.meta.json</c> sidecar, is what the catalogue and the Wiki read.
/// </summary>
public sealed class WorkbenchDecisionServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "workbench-decision-" + Guid.NewGuid().ToString("N"));

    public WorkbenchDecisionServiceTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    [Fact]
    public void Prepare_RejectsAnIncompleteFeatureDraftWithoutTouchingTheDescriptor()
    {
        WriteSchemaTwo("routing-policy");
        var (catalogue, decisions) = Services();
        var before = File.ReadAllText(Descriptor("routing-policy"));

        var result = decisions.Prepare("Project", "routing-policy", new PrepareWorkbenchDecisionRequest
        {
            OperationId = "workbench-ui-prepare-1",
            Outcome = "feature-spawn",
            Actor = "Robert",
            ExpectedFingerprint = catalogue.Read("Project", "routing-policy")!.Fingerprint,
            Task = new WorkbenchTaskDraft { Title = "Implement it", Goal = "" },
        });

        Assert.False(result.Success);
        Assert.Equal("validation", result.ErrorCode);
        Assert.Equal(before, File.ReadAllText(Descriptor("routing-policy")));
    }

    [Fact]
    public void Prepare_ValidatesAgainstTheCurrentFingerprintWithoutWriting()
    {
        WriteSchemaTwo("routing-policy");
        var (catalogue, decisions) = Services();
        var fingerprint = catalogue.Read("Project", "routing-policy")!.Fingerprint;

        var result = decisions.Prepare("Project", "routing-policy", FeaturePrepare(fingerprint));

        Assert.True(result.Success);
        Assert.Equal("prepared", result.DecisionStage);
        Assert.Equal(fingerprint, result.Fingerprint);
        Assert.Equal("Implement the routing policy", result.TaskDraft!.Title);
        Assert.Null(catalogue.List("Project", includeHistory: true)!.Items.Single().Decision);
    }

    [Fact]
    public void Confirm_WritesTheDecisionIntoWorkbenchJsonWithLifecycleHistory()
    {
        WriteSchemaTwo("routing-policy");
        var (catalogue, decisions) = Services();
        var fingerprint = catalogue.Read("Project", "routing-policy")!.Fingerprint;

        var result = decisions.Confirm("Project", "routing-policy", new ConfirmWorkbenchDecisionRequest
        {
            OperationId = "workbench-ui-confirm-1",
            Outcome = "feature-spawn",
            Actor = "Robert",
            ExpectedFingerprint = fingerprint,
            Task = FeaturePrepare(fingerprint).Task,
            Confirmed = true,
        });

        Assert.True(result.Success);
        Assert.Equal("succeeded", result.DecisionStage);

        using var written = JsonDocument.Parse(File.ReadAllText(Descriptor("routing-policy")));
        var descriptor = written.RootElement;
        Assert.Equal("decided", descriptor.GetProperty("lifecycleState").GetString());
        Assert.Equal("Robert", descriptor.GetProperty("editedBy").GetString());
        var history = descriptor.GetProperty("lifecycleHistory").EnumerateArray().ToList();
        Assert.Equal(2, history.Count);
        Assert.Equal("decided", history[^1].GetProperty("state").GetString());
        Assert.Contains("Implement the routing policy", history[^1].GetProperty("note").GetString());
        Assert.Equal("succeeded", descriptor.GetProperty("decision").GetProperty("state").GetString());

        // The descriptor still validates, so the catalogue projects the decision.
        var item = catalogue.List("Project", includeHistory: true)!.Items.Single();
        Assert.Equal("decided", item.Status);
        Assert.Equal("succeeded", item.DecisionStage);
        Assert.False(File.Exists(Descriptor("routing-policy") + ".meta.json"));
    }

    [Fact]
    public void Confirm_ArchivesALegacyDescriptorInPlaceInsteadOfASidecar()
    {
        WriteSchemaOne("legacy-experiment");
        var (catalogue, decisions) = Services();

        var result = decisions.Confirm("Project", "legacy-experiment", new ConfirmWorkbenchDecisionRequest
        {
            OperationId = "workbench-ui-archive-1",
            Outcome = "archive",
            Actor = "Robert",
            ExpectedFingerprint = catalogue.Read("Project", "legacy-experiment")!.Fingerprint,
            ArchiveReason = "The experiment disproved the direction.",
            Confirmed = true,
        });

        Assert.True(result.Success);
        Assert.Equal("archived", result.DecisionStage);
        using var written = JsonDocument.Parse(File.ReadAllText(Descriptor("legacy-experiment")));
        Assert.Equal("archived", written.RootElement.GetProperty("status").GetString());
        Assert.Equal("archived", catalogue.List("Project", includeHistory: true)!.Items.Single().Status);
        Assert.Empty(catalogue.List("Project")!.Items);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(Descriptor("legacy-experiment"))!, "*.meta.json"));
    }

    [Fact]
    public void Confirm_RefusesAFingerprintThatNoLongerMatchesTheFile()
    {
        WriteSchemaTwo("routing-policy");
        var (catalogue, decisions) = Services();
        var stale = catalogue.Read("Project", "routing-policy")!.Fingerprint;
        File.AppendAllText(Path.Combine(_root, "docs", "workbenches", "routing-policy", "index.html"),
            "<p>Someone edited the artifact.</p>");

        var result = decisions.Confirm("Project", "routing-policy", new ConfirmWorkbenchDecisionRequest
        {
            OperationId = "workbench-ui-confirm-2",
            Outcome = "feature-spawn",
            Actor = "Robert",
            ExpectedFingerprint = stale,
            Task = FeaturePrepare(stale).Task,
            Confirmed = true,
        });

        Assert.False(result.Success);
        Assert.Equal("stale-revision", result.ErrorCode);
        Assert.DoesNotContain("\"decision\"", File.ReadAllText(Descriptor("routing-policy")));
    }

    private static PrepareWorkbenchDecisionRequest FeaturePrepare(string? fingerprint) => new()
    {
        OperationId = "workbench-ui-prepare-2",
        Outcome = "feature-spawn",
        Actor = "Robert",
        ExpectedFingerprint = fingerprint,
        Task = new WorkbenchTaskDraft
        {
            Title = "Implement the routing policy",
            Goal = "Ship the confirmed direction.",
            AcceptanceCriteria = ["The direction is implemented and verified."],
            TargetProject = "Project",
        },
    };

    private string Descriptor(string id) =>
        Path.Combine(_root, "docs", "workbenches", id, "workbench.json");

    private void WriteSchemaTwo(string id)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{id}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {
            "schemaVersion": 2,
            "id": "{{id}}",
            "title": "Routing policy",
            "summary": "Choose the durable routing direction.",
            "entrypoint": "index.html",
            "pageKind": "workbench",
            "lifecycleState": "review-requested",
            "phase": "decision-ready",
            "editedBy": "Robert",
            "editedAt": "2026-07-26T10:00:00Z",
            "lifecycleHistory": [
              { "state": "review-requested", "editedBy": "Robert", "editedAt": "2026-07-26T10:00:00Z" }
            ]
          }
          """);
    }

    private void WriteSchemaOne(string id)
    {
        var dir = Path.Combine(_root, "docs", "workbenches", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "index.html"), $"<h1>{id}</h1>");
        File.WriteAllText(Path.Combine(dir, "workbench.json"), $$"""
          {"schemaVersion":1,"id":"{{id}}","title":"Legacy experiment","summary":"Question",
           "entrypoint":"index.html","status":"active","phase":"decision-ready","updatedAt":"2026-07-26T10:00:00Z"}
          """);
    }

    private (WorkbenchCatalogueService Catalogue, WorkbenchDecisionService Decisions) Services()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Project",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var catalogue = new WorkbenchCatalogueService(scanner, registry, git);
        return (catalogue, new WorkbenchDecisionService(catalogue, git));
    }
}
