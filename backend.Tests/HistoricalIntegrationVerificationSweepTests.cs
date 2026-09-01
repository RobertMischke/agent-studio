using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class HistoricalIntegrationVerificationSweepTests
{
    [Theory]
    [InlineData(true, true, false, false, false, false, IntegrationRecordClasses.IntegratedVerified)]
    [InlineData(true, true, true, false, false, false, IntegrationRecordClasses.IntegratedHistorical)]
    [InlineData(false, false, true, true, true, false, IntegrationRecordClasses.NoCodeExpected)]
    [InlineData(true, false, true, false, false, true, IntegrationRecordClasses.ContentOnFence)]
    [InlineData(true, false, true, false, true, false, IntegrationRecordClasses.GenuinelyMissing)]
    [InlineData(false, false, true, false, false, false, IntegrationRecordClasses.NoAttributionLegacy)]
    public void Policy_ClassifiesSixWayMatrix(
        bool hasCommits,
        bool allIntegrated,
        bool historical,
        bool noCodeExpected,
        bool hasDeliverables,
        bool hasFence,
        string expected)
    {
        Assert.Equal(
            expected,
            HistoricalIntegrationVerificationPolicy.Classify(new(
                hasCommits,
                allIntegrated,
                historical,
                noCodeExpected,
                hasDeliverables,
                hasFence)));
    }

    [Theory]
    [InlineData(IntegrationRecordClasses.IntegratedVerified, false)]
    [InlineData(IntegrationRecordClasses.IntegratedHistorical, false)]
    [InlineData(IntegrationRecordClasses.NoCodeExpected, false)]
    [InlineData(IntegrationRecordClasses.NoAttributionLegacy, false)]
    [InlineData(IntegrationRecordClasses.ContentOnFence, true)]
    [InlineData(IntegrationRecordClasses.GenuinelyMissing, true)]
    public void OperatorVisibleDetector_OnlyReturnsActionableHistoricalClasses(
        string classification,
        bool expected)
    {
        var task = new TaskInfo
        {
            IntegrationRecords =
            [
                new TaskIntegrationRecord
                {
                    Id = HistoricalIntegrationVerificationSweep.RecordId,
                    Classification = classification,
                    RecordedAtUtc = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
                },
            ],
        };

        Assert.Equal(
            expected,
            TaskIntegrationRecordDetector.LatestOperatorVisibleVerification(task) is not null);
    }

    [Theory]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(true, false, true, true, true, true)]
    [InlineData(true, false, true, true, false, false)]
    [InlineData(true, false, true, false, true, false)]
    [InlineData(true, true, true, true, true, false)]
    [InlineData(false, false, false, true, true, false)]
    public void CandidatePolicy_ExtendsV1CoverageOnlyToPreRecordingIntegrationPopulation(
        bool isTerminal,
        bool hasVerification,
        bool hasNativeRecord,
        bool integrationRequired,
        bool acceptedBeforeRecordingEra,
        bool expected)
    {
        Assert.Equal(
            expected,
            HistoricalIntegrationVerificationPolicy.IsSweepCandidate(new(
                isTerminal,
                hasVerification,
                hasNativeRecord,
                integrationRequired,
                acceptedBeforeRecordingEra)));
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task RunOnceAsync_ClassifiesInBatches_WritesCompactReport_AndIsIdempotent()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "historical-integration-sweep-" + Guid.NewGuid().ToString("N"));
        var repo = Path.Combine(root, "repo");
        var taskStore = Path.Combine(root, "task-store");
        var completed = Path.Combine(taskStore, TaskStates.Completed);
        var archive = Path.Combine(taskStore, TaskStates.Archive);
        var reportPath = Path.Combine(taskStore, ".metadata", "migrations", "report.json");
        Directory.CreateDirectory(repo);
        Directory.CreateDirectory(completed);
        Directory.CreateDirectory(archive);
        try
        {
            Git(repo, "init", "-q", "-b", "main");
            Git(repo, "config", "user.email", "test@example.com");
            Git(repo, "config", "user.name", "test");
            File.WriteAllText(Path.Combine(repo, "README.md"), "seed");
            Git(repo, "add", "README.md");
            Git(repo, "commit", "-q", "-m", "seed");
            Git(repo, "checkout", "-q", "-b", "develop");

            Git(repo, "checkout", "-q", "-b", "agent-studio/salvage/runner/AGT-3001/old");
            File.WriteAllText(Path.Combine(repo, "historical.cs"), "complete");
            Git(repo, "add", "historical.cs");
            Git(repo, "commit", "-q", "-m", "wip(runner): salvage before teardown - outcome Done");
            var obsoleteFenceSha = Git(repo, "rev-parse", "HEAD").Trim();

            Git(repo, "checkout", "-q", "develop");
            File.WriteAllText(Path.Combine(repo, "historical.cs"), "complete");
            Git(repo, "add", "historical.cs");
            Git(repo, "commit", "-q", "-m", "feat(AGT-3001): integrated historical delivery");
            var historicalSha = Git(repo, "rev-parse", "HEAD").Trim();

            File.WriteAllText(Path.Combine(repo, "verified.cs"), "complete");
            Git(repo, "add", "verified.cs");
            Git(repo, "commit", "-q", "-m", "feat(AGT-3002): integrated current delivery");
            var verifiedSha = Git(repo, "rev-parse", "HEAD").Trim();

            Git(repo, "checkout", "-q", "-b", "agent-studio/results/runner/AGT-3003/fence-1");
            File.WriteAllText(Path.Combine(repo, "fenced.cs"), "not integrated");
            Git(repo, "add", "fenced.cs");
            Git(repo, "commit", "-q", "-m", "feat(AGT-3003): fenced delivery");
            var fencedSha = Git(repo, "rev-parse", "HEAD").Trim();
            Git(repo, "checkout", "-q", "develop");

            SeedTask(completed, "agt-3001", "AGT-3001", "2026-08-09T12:00:00Z", commits:
            [
                Commit(obsoleteFenceSha, "wip(runner): salvage before teardown - outcome Done", "round-1", "historical.cs"),
                Commit(historicalSha, "feat(AGT-3001): integrated historical delivery", "round-2", "historical.cs"),
            ]);
            SeedTask(completed, "agt-3002", "AGT-3002", "2026-08-10T02:00:00Z", commits:
            [
                Commit(verifiedSha, "feat(AGT-3002): integrated current delivery", "round-1", "verified.cs"),
            ]);
            SeedTask(completed, "agt-3003", "AGT-3003", "2026-08-09T12:00:00Z", commits:
            [
                Commit(
                    fencedSha,
                    "feat(AGT-3003): fenced delivery",
                    "round-1",
                    "fenced.cs",
                    "agent-studio/results/runner/AGT-3003/fence-1"),
            ]);
            var reportOnly = SeedTask(
                completed,
                "agt-3004",
                "AGT-3004",
                "2026-08-09T12:00:00Z",
                mode: TaskModes.Research);
            Directory.CreateDirectory(Path.Combine(reportOnly, "results"));
            File.WriteAllText(Path.Combine(reportOnly, "results", "report.md"), "# Findings");
            SeedTask(completed, "agt-3005", "AGT-3005", "2026-08-09T12:00:00Z", commits:
            [
                Commit(fencedSha, "feat(AGT-3005): delivery without surviving ref", "round-1", "missing.cs"),
            ]);

            Git(repo, "branch", "task/AGT-3006", "develop");
            var legacyIntegrated = SeedTask(
                archive,
                "agt-3006",
                "AGT-3006",
                "2026-08-09T12:00:00Z",
                state: TaskStates.Archive);
            AppendIntegrationStarted(legacyIntegrated, "2026-08-09T12:00:00Z");

            var legacyUnattributed = SeedTask(
                archive,
                "agt-3007",
                "AGT-3007",
                "2026-08-09T12:00:00Z",
                state: TaskStates.Archive);
            AppendIntegrationStarted(legacyUnattributed, "2026-08-09T12:00:00Z");

            var currentStall = SeedTask(
                completed,
                "agt-3008",
                "AGT-3008",
                "2026-08-11T12:00:00Z");
            AppendIntegrationStarted(currentStall, "2026-08-11T12:00:00Z");

            SeedTask(
                completed,
                "agt-3009",
                "AGT-3009",
                "2026-08-09T12:00:00Z",
                integrationRecords:
                [
                    new TaskIntegrationRecord
                    {
                        Id = HistoricalIntegrationVerificationSweep.PreviousRecordId,
                        Classification = IntegrationRecordClasses.NoCodeExpected,
                        RecordedAtUtc = new DateTime(2026, 8, 10, 10, 0, 0, DateTimeKind.Utc),
                        Evidence = "Classified by the completed v1 migration.",
                    },
                ]);

            var configuration = new ConfigurationBuilder().AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = taskStore,
                    ["WatchPaths:0:Name"] = "Fixture",
                    ["WatchPaths:0:Path"] = taskStore,
                    ["WatchPaths:0:RootPath"] = repo,
                    ["WatchPaths:0:RepositoryPath"] = repo,
                }).Build();
            var scanner = new TaskScannerService(
                configuration,
                NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
            var git = new GitService(NullLogger<GitService>.Instance, scanner, configuration);
            var settings = new ProjectSettingsService(
                NullLogger<ProjectSettingsService>.Instance,
                configuration);
            settings.SetIntegrationBranch("Fixture", "develop");
            var mutations = new TaskMutationService(
                scanner,
                new ClientIdentityStore(configuration, NullLogger<ClientIdentityStore>.Instance),
                new AgentStudio.Registry.ProjectRegistry(
                    configuration,
                    NullLogger<AgentStudio.Registry.ProjectRegistry>.Instance),
                new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance),
                NullLogger<TaskMutationService>.Instance,
                git: git);
            var sweep = new HistoricalIntegrationVerificationSweep(
                scanner,
                mutations,
                git,
                settings,
                new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance),
                new TimelineLog(NullLogger<TimelineLog>.Instance),
                reportPath,
                batchSize: 2,
                new FixedTimeProvider(new DateTimeOffset(2026, 8, 10, 10, 0, 0, TimeSpan.Zero)),
                NullLogger<HistoricalIntegrationVerificationSweep>.Instance);

            var first = await sweep.RunOnceAsync();
            var reportAfterFirstRun = File.ReadAllText(reportPath);
            var tasksAfterFirstRun = Directory
                .EnumerateFiles(taskStore, "task.json", SearchOption.AllDirectories)
                .ToDictionary(path => path, File.ReadAllText, StringComparer.Ordinal);
            var second = await sweep.RunOnceAsync();

            Assert.True(first.Completed);
            Assert.Equal(2, first.Version);
            Assert.Equal(9, first.ScannedCards);
            Assert.Equal(8, first.CandidateCards);
            Assert.Equal(7, first.RecordsWritten);
            Assert.Equal(4, first.BatchCount);
            Assert.Equal(2, first.Counts[IntegrationRecordClasses.IntegratedHistorical]);
            Assert.Equal(1, first.Counts[IntegrationRecordClasses.IntegratedVerified]);
            Assert.Equal(2, first.Counts[IntegrationRecordClasses.NoCodeExpected]);
            Assert.Equal(1, first.Counts[IntegrationRecordClasses.ContentOnFence]);
            Assert.Equal(1, first.Counts[IntegrationRecordClasses.GenuinelyMissing]);
            Assert.Equal(1, first.Counts[IntegrationRecordClasses.NoAttributionLegacy]);
            Assert.Equal(2, first.OperatorItems.Count);
            Assert.All(first.OperatorItems, item =>
                Assert.True(IntegrationRecordClasses.IsOperatorVisible(item.Classification)));
            Assert.True(second.AlreadyCompleted);
            Assert.Equal(0, second.RecordsWritten);
            Assert.Equal(reportAfterFirstRun, File.ReadAllText(reportPath));
            Assert.All(tasksAfterFirstRun, row => Assert.Equal(row.Value, File.ReadAllText(row.Key)));

            foreach (var id in Enumerable.Range(3001, 7))
            {
                var task = scanner.FindJob($"agt-{id}", taskStore);
                var record = Assert.Single(task!.IntegrationRecords);
                Assert.Equal(HistoricalIntegrationVerificationSweep.RecordId, record.Id);
            }
            Assert.Empty(scanner.FindJob("agt-3008", taskStore)!.IntegrationRecords);
            var previousRecord = Assert.Single(scanner.FindJob("agt-3009", taskStore)!.IntegrationRecords);
            Assert.Equal(HistoricalIntegrationVerificationSweep.PreviousRecordId, previousRecord.Id);
            Assert.Equal(
                IntegrationRecordClasses.IntegratedHistorical,
                scanner.FindJob("agt-3001", taskStore)!.IntegrationRecords.Single().Classification);
            var historicalRecord = scanner.FindJob("agt-3001", taskStore)!.IntegrationRecords.Single();
            Assert.DoesNotContain(obsoleteFenceSha, historicalRecord.CommitShas);
            Assert.Contains(historicalSha, historicalRecord.CommitShas);
            Assert.Single(
                scanner.FindJob("agt-3003", taskStore)!.IntegrationRecords.Single().FenceRefs);
            var fallbackRecord = scanner.FindJob("agt-3006", taskStore)!.IntegrationRecords.Single();
            Assert.Equal(IntegrationRecordClasses.IntegratedHistorical, fallbackRecord.Classification);
            Assert.NotEmpty(fallbackRecord.CommitShas);
            Assert.Contains(fallbackRecord.FenceRefs, reference =>
                reference.EndsWith("task/AGT-3006", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(
                IntegrationRecordClasses.NoAttributionLegacy,
                scanner.FindJob("agt-3007", taskStore)!.IntegrationRecords.Single().Classification);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception ex) { SilentCatch.Note(ex, "HistoricalIntegrationVerificationSweepTests cleanup"); }
        }
    }

    private static string SeedTask(
        string lane,
        string id,
        string key,
        string enteredLaneAt,
        IReadOnlyList<object>? commits = null,
        string mode = TaskModes.Coding,
        string state = TaskStates.Completed,
        IReadOnlyList<TaskIntegrationRecord>? integrationRecords = null)
    {
        var folder = Path.Combine(lane, id);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            key,
            title = $"Fixture {key}",
            state,
            order = 1,
            agent = "codex",
            mode,
            createdAt = enteredLaneAt,
            enteredLaneAt,
            commits = commits ?? [],
            integrationRecords = integrationRecords ?? [],
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Fixture task.");
        return folder;
    }

    private static void AppendIntegrationStarted(string folder, string timestamp)
    {
        var timeline = new TimelineLog(NullLogger<TimelineLog>.Instance);
        Assert.True(timeline.Append(folder, new TimelineEvent
        {
            Ts = DateTime.Parse(timestamp).ToUniversalTime(),
            Kind = TimelineEventKinds.IntegrationStarted,
            Actor = TimelineActors.System,
            Summary = "Legacy acceptance integration started.",
            Details = new Dictionary<string, string> { ["stage"] = "acceptance" },
        }));
    }

    private static object Commit(
        string sha,
        string message,
        string runAttemptId,
        string file,
        string? branch = null)
        => new
        {
            sha,
            shortSha = sha[..7],
            message,
            branch,
            filesChanged = 1,
            files = new[] { file },
            at = "2026-08-09T10:00:00Z",
            runAttemptId,
        };

    private static string Git(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)} failed: {error}");
        return output;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
