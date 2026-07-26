using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class TaskServerStoreTests
{
    [Fact]
    public async Task Releasing_a_dead_runner_attempt_returns_its_progress_task_to_ready()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);

        await store.ReleaseLeaseAsync(
            claim.Run!.RunId,
            new LeaseReleaseRequest(
                "runner-a", "instance-a", claim.Lease!.LeaseId, claim.Lease.Fence,
                "runner-process-missing"),
            "runner-a",
            default);

        var released = await store.GetTaskAsync(project.ProjectId, task.TaskId, default);
        Assert.Equal("2-ready", released!.State);
        var replacement = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        Assert.Equal("claimed", replacement.Status);
        Assert.Equal(task.TaskId, replacement.Task!.TaskId);
        Assert.True(replacement.Lease!.Fence > claim.Lease.Fence);
    }

    [Fact]
    public async Task Schema_migration_is_recorded_and_a_newer_store_fails_closed()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        Assert.Equal(TaskServerStore.CurrentSchemaVersion, store.Status().SchemaVersion);

        await using (var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"SELECT count(*) FROM schema_migrations WHERE version = {TaskServerStore.CurrentSchemaVersion};";
            Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
            command.CommandText = $"UPDATE meta SET value = '{TaskServerStore.CurrentSchemaVersion + 1}' WHERE key = 'schema_version';";
            await command.ExecuteNonQueryAsync();
        }

        var olderBinary = Store(temp.Path);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => olderBinary.InitializeAsync());
        Assert.Contains("newer than this service supports", error.Message);
        Assert.False(olderBinary.AuthorityReady);
    }

    [Fact]
    public async Task Repeated_startup_applies_each_schema_migration_once()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        var second = Store(temp.Path);
        await second.InitializeAsync();

        await using var connection = new SqliteConnection(
            $"Data Source={first.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT count(*) FROM schema_migrations WHERE version = {TaskServerStore.CurrentSchemaVersion};";
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Backup_initialization_does_not_quarantine_live_authority()
    {
        using var temp = new TempDirectory();
        var servingStore = Store(temp.Path);
        await servingStore.InitializeAsync();
        await SeedReadyTaskAsync(servingStore);
        await servingStore.RegisterRunnerAsync(
            "runner-a",
            Runner("instance-a"),
            "test",
            default);
        var claim = await servingStore.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "test",
            default);

        var backupProcessStore = Store(temp.Path);
        await backupProcessStore.InitializeForBackupAsync();
        await backupProcessStore.CreateBackupAsync(
            new BackupRequest("timer"),
            "timer",
            default);

        var renewed = await servingStore.RenewLeaseAsync(
            claim.Run!.RunId,
            new LeaseRenewRequest(
                "runner-a",
                "instance-a",
                claim.Lease!.LeaseId,
                claim.Lease.Fence),
            "runner-a",
            default);
        Assert.Equal("active", renewed.Lease!.Status);
    }

    [Fact]
    public async Task Restart_restores_fence_authority_and_quarantines_the_attempt()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        var (_, _, task) = await SeedReadyTaskAsync(first);
        await first.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await first.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);

        Assert.Equal("claimed", claim.Status);
        Assert.NotNull(claim.Lease);
        var run = claim.Run!;
        var lease = claim.Lease!;

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        Assert.True(restarted.AuthorityReady);
        Assert.Equal(first.ServerId, restarted.ServerId);

        await restarted.RegisterRunnerAsync("runner-b", Runner("instance-b"), "test", default);
        var contender = await restarted.ClaimAsync(new ClaimRequest("runner-b", "instance-b"), "test", default);
        Assert.Equal("empty", contender.Status);

        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() => restarted.IngestEventAsync(
            run.RunId,
            new EventIngestRequest("evt-stale", "test", "{}", "stale-1", lease.Fence),
            "runner-a",
            default));
        Assert.Equal("lease-not-active", stale.Code);

        var staleDecision = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            run.RunId,
            ExecutionAttemptKind.Coding,
            ExitCode: -1,
            StdErr: "provider process terminated during Task Server restart"));
        var staleCompletion = await Assert.ThrowsAsync<TaskServerConflictException>(() => restarted.CompleteRunAsync(
            run.RunId,
            new CompleteRunRequest(
                "runner-a",
                "instance-stale",
                lease.LeaseId,
                lease.Fence,
                staleDecision.Outcome.ToString(),
                IdempotencyKey: $"completion:{run.RunId}:stale",
                Sequence: 1,
                OutcomeDecision: staleDecision),
            "runner-a",
            default));
        Assert.Equal("stale-fence", staleCompletion.Code);

        await restarted.ResolveUnknownAttemptAsync(
            run.RunId,
            new ResolveUnknownAttemptRequest("systemd unit is inactive and the cgroup is empty"),
            "operator",
            default);
        var replacement = await restarted.ClaimAsync(new ClaimRequest("runner-b", "instance-b"), "test", default);
        Assert.Equal("claimed", replacement.Status);
        Assert.True(replacement.Lease!.Fence > lease.Fence);
        Assert.Equal(task.TaskId, replacement.Task!.TaskId);
    }

    [Fact]
    public async Task Backup_restore_preserves_resources_events_artifacts_audit_identity_and_fences()
    {
        await TempDirectory.RunAsync(async temp =>
        {
            var store = Store(temp.Path);
            await store.InitializeAsync();
            var (_, project, task) = await SeedReadyTaskAsync(store);
            await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
            var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
            var lease = claim.Lease!;
            var runId = claim.Run!.RunId;

            await store.IngestEventAsync(runId, new EventIngestRequest("evt-1", "runner.output", "{\"text\":\"hello\"}", "event-1", lease.Fence), "runner-a", default);
            var bytes = Encoding.UTF8.GetBytes("evidence");
            var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            await store.IngestArtifactAsync(runId, new ArtifactIngestRequest("art-1", "result.txt", "text/plain", Convert.ToBase64String(bytes), sha, "artifact-1", lease.Fence), "runner-a", default);
            var handoff = await store.AcknowledgeResultHandoffAsync(
                runId, Handoff(runId, lease, sequence: 3), "runner-a", default);
            await store.CompleteRunAsync(runId, new CompleteRunRequest(
                "runner-a", "instance-a", lease.LeaseId, lease.Fence, "success",
                ResultEnvelopeDigest: handoff.EnvelopeDigest,
                IdempotencyKey: $"completion:{runId}",
                Sequence: 4), "runner-a", default);

            var serverId = store.ServerId;
            var backup = await store.CreateBackupAsync(new BackupRequest("acceptance"), "operator", default);
            await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("must disappear after restore"), "test", default);
            await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "restore rehearsal"), "operator", default);
            var restored = await store.RestoreBackupAsync(new RestoreRequest(backup.BackupId), "operator", default);

            Assert.True(restored.Verified);
            Assert.True(restored.Restored);
            Assert.Equal(TaskServerMode.Maintenance, store.Mode);
            Assert.Equal(serverId, store.ServerId);
            Assert.Single(await store.ListTasksAsync(project.ProjectId, default));
            var restoredEvents = await store.ListEventsAsync(runId, 0, default);
            Assert.Equal(3, restoredEvents.Count);
            Assert.Contains(restoredEvents, item => item.Kind == LifecycleEventKinds.RunCompleted);
            Assert.Contains(restoredEvents, item => item.Kind == LifecycleEventKinds.PostProcessingCompleted);
            Assert.Single(await store.ListArtifactsAsync(runId, default));
            Assert.Contains(await store.ListAuditAsync(0, default), record => record.Action == "run.claimed");
            Assert.Equal(task.TaskId, (await store.GetTaskAsync(project.ProjectId, task.TaskKey, default))!.TaskId);
            var history = await store.GetTaskHistoryAsync(project.ProjectId, task.TaskKey, 0, default);
            Assert.NotNull(history);
            Assert.Single(history.Runs);
            Assert.Equal(restoredEvents, history.Events);
            Assert.Single(history.Artifacts);
            Assert.Contains(history.Audit, record => record.Action == "run.completed");
            Assert.Equal(restoredEvents[^1].Cursor, history.LastCursor);

            var incremental = await store.GetTaskHistoryAsync(
                project.ProjectId,
                task.TaskKey,
                restoredEvents[0].Cursor,
                default);
            Assert.NotNull(incremental);
            Assert.All(incremental.Events, item => Assert.True(item.Cursor > restoredEvents[0].Cursor));
        });
    }

    [Fact]
    public async Task Backup_and_restore_release_database_file_handles_before_cleanup()
    {
        await TempDirectory.RunAsync(async temp =>
        {
            var store = Store(temp.Path);
            await store.InitializeAsync();
            await SeedReadyTaskAsync(store);

            var backup = await store.CreateBackupAsync(new BackupRequest("handle-check"), "operator", default);
            AssertCanOpenExclusively(backup.Path);

            await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "restore handle check"), "operator", default);
            var restored = await store.RestoreBackupAsync(new RestoreRequest(backup.BackupId), "operator", default);

            Assert.True(restored.Restored);
            AssertCanOpenExclusively(backup.Path);
            AssertCanOpenExclusively(store.DatabasePath);
        });
    }

    [Fact]
    public async Task Legacy_migration_rehearses_inventory_freeze_import_integrity_and_evidence_git_preservation()
    {
        await TempDirectory.RunAsync(async data =>
        {
            await TempDirectory.RunAsync(async legacy =>
            {
                var taskDirectory = Path.Combine(legacy.Path, "projects", "agent-studio", "2-ready", "AGT-1");
                Directory.CreateDirectory(Path.Combine(taskDirectory, "results"));
                Directory.CreateDirectory(Path.Combine(legacy.Path, ".git"));
                await File.WriteAllTextAsync(Path.Combine(legacy.Path, ".git", "HEAD"), "ref: refs/heads/main\n");
                await File.WriteAllTextAsync(Path.Combine(taskDirectory, "job.json"), """
                    {"id":"AGT-1","title":"Migrated task","state":"2-ready","projectName":"Agent Studio"}
                    """);
                await File.WriteAllTextAsync(Path.Combine(taskDirectory, "prompt.md"), "Migrated prompt");
                await File.WriteAllTextAsync(Path.Combine(taskDirectory, "timeline.jsonl"), "{\"kind\":\"created\",\"timestamp\":\"2026-07-17T10:00:00Z\"}\n");
                await File.WriteAllTextAsync(Path.Combine(taskDirectory, "results", "evidence.txt"), "proof");

                var store = Store(data.Path);
                await store.InitializeAsync();
                var migration = new LegacyMigrationService(store);
                var request = new LegacyMigrationRequest(legacy.Path, "Agent Studio for Software", true);
                var inventory = await migration.InventoryAsync(request, default);
                Assert.Equal(1, inventory.Projects);
                Assert.Equal(1, inventory.Tasks);
                Assert.Equal(1, inventory.Events);
                Assert.Equal(1, inventory.Artifacts);

                request = request with { ExpectedMigrationId = inventory.MigrationId };
                await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "single-writer cutover"), "operator", default);
                var result = await migration.ImportAsync(request, "operator", default);
                Assert.True(result.Imported);
                Assert.False(string.IsNullOrWhiteSpace(result.IntegritySha256));
                Assert.Contains("Restore backup", result.RollbackBoundary);
                Assert.True(Directory.Exists(Path.Combine(data.Path, "migration-evidence", result.MigrationId)));

                var project = Assert.Single(await store.ListProjectsAsync(null, default));
                var migrated = Assert.Single(await store.ListTasksAsync(project.ProjectId, default));
                Assert.Equal("AGT-1", migrated.TaskKey);
                Assert.Equal("Migrated prompt", migrated.Body);
                Assert.Single(await store.ListEventsAsync(string.Empty, 0, default));
                Assert.Single(await store.ListArtifactsAsync(string.Empty, default));

                var migrationBackup = Assert.Single(Directory.EnumerateFiles(
                    store.BackupDirectory,
                    "*-before-legacy-import-*.db",
                    SearchOption.TopDirectoryOnly));
                AssertCanOpenExclusively(migrationBackup);
                AssertCanOpenExclusively(store.DatabasePath);
            });
        });
    }

    [Fact]
    public async Task Legacy_import_rejects_a_source_that_changed_after_inventory()
    {
        using var data = new TempDirectory();
        using var legacy = new TempDirectory();
        var taskDirectory = Path.Combine(legacy.Path, "projects", "agent-studio", "2-ready", "AGT-1");
        Directory.CreateDirectory(taskDirectory);
        await File.WriteAllTextAsync(Path.Combine(taskDirectory, "job.json"), """
            {"id":"AGT-1","title":"Migrated task","state":"2-ready","projectName":"Agent Studio"}
            """);
        var prompt = Path.Combine(taskDirectory, "prompt.md");
        await File.WriteAllTextAsync(prompt, "Original prompt");

        var store = Store(data.Path);
        await store.InitializeAsync();
        var migration = new LegacyMigrationService(store);
        var request = new LegacyMigrationRequest(legacy.Path, "Agent Studio for Software", true);
        var inventory = await migration.InventoryAsync(request, default);
        await File.WriteAllTextAsync(prompt, "Changed prompt with a different length");

        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "single-writer cutover"), "operator", default);
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => migration.ImportAsync(
            request with { ExpectedMigrationId = inventory.MigrationId },
            "operator",
            default));

        Assert.Equal("legacy-inventory-changed", conflict.Code);
        Assert.Empty(await store.ListProjectsAsync(null, default));
    }

    [Fact]
    public async Task Failed_restore_rolls_back_to_the_live_store_and_remains_ready_in_maintenance()
    {
        await TempDirectory.RunAsync(async temp =>
        {
            var store = Store(temp.Path);
            await store.InitializeAsync();
            var (_, project, _) = await SeedReadyTaskAsync(store);
            var backup = await store.CreateBackupAsync(new BackupRequest("future-schema"), "operator", default);
            await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("must survive failed restore"), "test", default);

            await using (var connection = new SqliteConnection($"Data Source={backup.Path};Pooling=False"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "UPDATE meta SET value = '999' WHERE key = 'schema_version';";
                await command.ExecuteNonQueryAsync();
                command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                await command.ExecuteNonQueryAsync();
            }

            var serverId = store.ServerId;
            await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Maintenance, "restore rehearsal"), "operator", default);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.RestoreBackupAsync(new RestoreRequest(backup.BackupId), "operator", default));

            Assert.Contains("newer than this service supports", error.Message);
            Assert.True(store.AuthorityReady);
            Assert.Equal(TaskServerMode.Maintenance, store.Mode);
            Assert.Equal(serverId, store.ServerId);
            Assert.Equal(2, (await store.ListTasksAsync(project.ProjectId, default)).Count);
            AssertCanOpenExclusively(backup.Path);
            AssertCanOpenExclusively(store.DatabasePath);
        });
    }

    [Fact]
    public async Task Idempotency_keys_replay_the_same_ingest_and_reject_cross_run_aliases()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, _) = await SeedReadyTaskAsync(store);
        await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Second task", "Do more work", "2-ready"), "test", default);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var first = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var second = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var firstRun = first.Run!;
        var firstLease = first.Lease!;
        var secondRun = second.Run!;
        var secondLease = second.Lease!;

        var eventRequest = new EventIngestRequest("evt-1", "runner.output", "{\"text\":\"hello\"}", "shared-event-key", firstLease.Fence);
        var eventCreated = await store.IngestEventAsync(firstRun.RunId, eventRequest, "runner-a", default);
        var eventReplay = await store.IngestEventAsync(firstRun.RunId, eventRequest with { EventId = "evt-retry" }, "runner-a", default);
        Assert.Equal(eventCreated.EventId, eventReplay.EventId);
        var eventConflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.IngestEventAsync(
            secondRun.RunId,
            eventRequest with { EventId = "evt-other", Fence = secondLease.Fence },
            "runner-a",
            default));
        Assert.Equal("idempotency-conflict", eventConflict.Code);

        var bytes = Encoding.UTF8.GetBytes("evidence");
        var sha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var artifactRequest = new ArtifactIngestRequest(
            "art-1", "result.txt", "text/plain", Convert.ToBase64String(bytes), sha, "shared-artifact-key", firstLease.Fence);
        var artifactCreated = await store.IngestArtifactAsync(firstRun.RunId, artifactRequest, "runner-a", default);
        var artifactReplay = await store.IngestArtifactAsync(firstRun.RunId, artifactRequest with { ArtifactId = "art-retry" }, "runner-a", default);
        Assert.Equal(artifactCreated.ArtifactId, artifactReplay.ArtifactId);
        var artifactConflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.IngestArtifactAsync(
            secondRun.RunId,
            artifactRequest with { ArtifactId = "art-other", Fence = secondLease.Fence },
            "runner-a",
            default));
        Assert.Equal("idempotency-conflict", artifactConflict.Code);

        var audit = await store.ListAuditAsync(0, default);
        Assert.Single(audit, record => record.Action == "event.ingested");
        Assert.Single(audit, record => record.Action == "artifact.ingested");
    }

    [Fact]
    public async Task Typed_outcome_completion_is_fenced_idempotent_and_survives_restart_with_raw_facts()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var run = claim.Run!;
        var lease = claim.Lease!;

        var wrongIdentity = ExecutionOutcomeAdapter.Classify(new ExecutionRawFacts(
            "run-other",
            ExecutionAttemptKind.Coding,
            StdErr: "HTTP 401 Missing bearer authentication",
            ExitCode: 1,
            DurableOutputState: DurableOutputState.Published,
            DurableOutputReference: "refs/heads/runner/test"));
        var identityConflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CompleteRunAsync(
            run.RunId,
            new CompleteRunRequest(
                "runner-a",
                "instance-a",
                lease.LeaseId,
                lease.Fence,
                ExecutionOutcomeKind.AuthenticationFailure.ToString(),
                IdempotencyKey: $"completion:{run.RunId}:wrong-attempt",
                Sequence: 1,
                OutcomeDecision: wrongIdentity),
            "runner-a",
            default));
        Assert.Equal("attempt-identity-mismatch", identityConflict.Code);

        var decision = ExecutionOutcomeAdapter.Classify(wrongIdentity.RawFacts with { AttemptId = run.RunId });
        var outcomeConflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CompleteRunAsync(
            run.RunId,
            new CompleteRunRequest(
                "runner-a",
                "instance-a",
                lease.LeaseId,
                lease.Fence,
                ExecutionOutcomeKind.SuccessfulCompletion.ToString(),
                IdempotencyKey: $"completion:{run.RunId}:outcome-mismatch",
                Sequence: 1,
                OutcomeDecision: decision),
            "runner-a",
            default));
        Assert.Equal("outcome-decision-mismatch", outcomeConflict.Code);

        var request = new CompleteRunRequest(
            "runner-a",
            "instance-a",
            lease.LeaseId,
            lease.Fence,
            decision.Outcome.ToString(),
            "provider capability is unavailable",
            IdempotencyKey: $"completion:{run.RunId}:typed-outcome",
            Sequence: 1,
            OutcomeDecision: decision);

        var completed = await store.CompleteRunAsync(run.RunId, request, "runner-a", default);
        var replay = await store.CompleteRunAsync(run.RunId, request, "runner-a", default);
        var conflictingReplay = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CompleteRunAsync(
            run.RunId,
            request with
            {
                OutcomeDecision = ExecutionOutcomeAdapter.Classify(
                    decision.RawFacts with { StdErr = "HTTP 401 different replay facts" }),
            },
            "runner-a",
            default));

        Assert.Equal(ExecutionOutcomeKind.AuthenticationFailure.ToString(), completed.Status);
        Assert.Equal(completed, replay);
        Assert.Equal("completion-conflict", conflictingReplay.Code);
        var firstEvents = await store.ListEventsAsync(run.RunId, 0, default);
        var classified = Assert.Single(firstEvents, item => item.Kind == "execution.outcome.classified");
        using (var payload = JsonDocument.Parse(classified.PayloadJson))
        {
            Assert.Equal(ExecutionOutcomeAdapter.Version, payload.RootElement.GetProperty("classifierVersion").GetString());
            Assert.Equal("authenticationFailure", payload.RootElement.GetProperty("outcome").GetString());
            Assert.Equal("waitForCapabilityRecovery", payload.RootElement.GetProperty("recoveryAction").GetString());
            Assert.Equal("high", payload.RootElement.GetProperty("confidence").GetString());
            Assert.Equal(
                "HTTP 401 Missing bearer authentication",
                payload.RootElement.GetProperty("rawFacts").GetProperty("stdErr").GetString());
        }

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        var replayedEvents = await restarted.ListEventsAsync(run.RunId, 0, default);
        Assert.Equal(classified, Assert.Single(replayedEvents, item => item.Kind == "execution.outcome.classified"));
        var timeline = Assert.Single(await restarted.ListAttemptsAsync(project.ProjectId, task.TaskKey, default));
        Assert.Equal(run.RunId, timeline.Run.RunId);
        Assert.Equal(ExecutionOutcomeKind.AuthenticationFailure, timeline.OutcomeDecision!.Outcome);
        Assert.Equal(ExecutionRecoveryAction.WaitForCapabilityRecovery, timeline.OutcomeDecision.RecoveryAction);
        Assert.Equal("HTTP 401 Missing bearer authentication", timeline.OutcomeDecision.RawFacts.StdErr);
    }

    [Fact]
    public async Task Typed_event_payloads_fail_before_persistence_when_the_bound_is_exceeded()
    {
        using var temp = new TempDirectory();
        var store = new TaskServerStore(
            Options.Create(new TaskServerOptions
            {
                DataDirectory = temp.Path,
                MaximumEventPayloadBytes = 128,
            }),
            TimeProvider.System);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "test",
            default);

        var error = await Assert.ThrowsAsync<ArgumentException>(() => store.IngestEventAsync(
            claim.Run!.RunId,
            new EventIngestRequest(
                "evt-oversized",
                LifecycleEventKinds.ToolTrace,
                new string('x', 129),
                "oversized-event",
                claim.Lease!.Fence),
            "runner-a",
            default));

        Assert.Contains("128-byte limit", error.Message);
        Assert.Empty(await store.ListEventsAsync(claim.Run!.RunId, 0, default));
    }

    [Fact]
    public async Task Drain_stops_new_admission_allows_completion_and_prepares_safe_shutdown()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        await store.ChangeModeAsync(new ChangeModeRequest(TaskServerMode.Draining, "upgrade"), "operator", default);

        var admission = await Assert.ThrowsAsync<TaskServerConflictException>(
            () => store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default));
        Assert.Equal("admission-closed", admission.Code);

        var lease = claim.Lease!;
        var inventoryPulse = await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "instance-a",
                AvailableSlots: 0,
                Inventory: new RunnerProcessInventory(
                    DateTime.UtcNow,
                    [new RunnerProcessInfo(
                        claim.Run!.RunId,
                        claim.Task!.TaskKey,
                        4242,
                        "/worktrees/active",
                        DateTime.UtcNow)])),
            "runner-a",
            default);
        Assert.Equal("empty", inventoryPulse.Status);
        var renewed = await store.RenewLeaseAsync(
            claim.Run!.RunId,
            new LeaseRenewRequest(
                "runner-a",
                "instance-a",
                lease.LeaseId,
                lease.Fence),
            "runner-a",
            default);
        Assert.Equal("renewed", renewed.Status);

        var handoff = await store.AcknowledgeResultHandoffAsync(
            claim.Run!.RunId,
            Handoff(claim.Run.RunId, lease, sequence: 1),
            "runner-a",
            default);
        await store.CompleteRunAsync(
            claim.Run.RunId,
            new CompleteRunRequest(
                "runner-a", "instance-a", lease.LeaseId, lease.Fence, "success",
                ResultEnvelopeDigest: handoff.EnvelopeDigest,
                IdempotencyKey: $"completion:{claim.Run.RunId}",
                Sequence: 2),
            "runner-a",
            default);
        var prepared = await store.PrepareShutdownAsync(new PrepareShutdownRequest("upgrade"), "operator", default);
        Assert.True(prepared.SafeToStop);
        Assert.Equal(TaskServerMode.Maintenance, prepared.Mode);
    }

    [Fact]
    public async Task Lost_handoff_ack_replays_one_envelope_and_one_lane_transition()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var run = claim.Run!;
        var lease = claim.Lease!;
        var request = Handoff(run.RunId, lease, sequence: 4);

        var first = await store.AcknowledgeResultHandoffAsync(run.RunId, request, "runner-a", default);
        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        var replay = await restarted.AcknowledgeResultHandoffAsync(
            run.RunId, request, "runner-a", default);

        Assert.False(first.Replay);
        Assert.True(replay.Replay);
        Assert.Equal(first.EnvelopeDigest, replay.EnvelopeDigest);
        Assert.Equal(first.AcknowledgedAt, replay.AcknowledgedAt);
        var reconstructable = await restarted.GetResultHandoffAsync(run.RunId, default);
        Assert.NotNull(reconstructable);
        Assert.Equal(request.Envelope.RepositoryId, reconstructable.Envelope.RepositoryId);
        Assert.Equal(request.Envelope.ResultSha, reconstructable.Envelope.ResultSha);
        Assert.Equal(request.Envelope.ImmutableRemoteRef, reconstructable.Envelope.ImmutableRemoteRef);

        var completion = new CompleteRunRequest(
            "runner-a", "instance-a", lease.LeaseId, lease.Fence, "success",
            ResultEnvelopeDigest: first.EnvelopeDigest,
            IdempotencyKey: $"completion:{run.RunId}",
            Sequence: 5);
        await restarted.CompleteRunAsync(run.RunId, completion, "runner-a", default);
        await restarted.CompleteRunAsync(run.RunId, completion, "runner-a", default);

        var audit = await restarted.ListAuditAsync(0, default);
        Assert.Single(audit, record => record.Action == "result-handoff.acknowledged");
        Assert.Single(audit, record => record.Action == "run.completed");
    }

    [Fact]
    public async Task Restart_completes_from_a_locally_durable_handoff_ack_without_replaying_handoff()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(first);
        await first.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await first.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var run = claim.Run!;
        var lease = claim.Lease!;
        var acknowledgement = await first.AcknowledgeResultHandoffAsync(
            run.RunId,
            Handoff(run.RunId, lease, sequence: 4),
            "runner-a",
            default);

        // The runner persisted the acknowledgement before both processes
        // restarted. Recovery must continue directly with the journaled
        // completion instead of requiring a duplicate handoff request merely
        // to reactivate the process-unknown lease.
        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        var completion = new CompleteRunRequest(
            lease.RunnerId,
            lease.InstanceId,
            lease.LeaseId,
            lease.Fence,
            "success",
            ResultEnvelopeDigest: acknowledgement.EnvelopeDigest,
            IdempotencyKey: $"completion:{run.RunId}",
            Sequence: 5);

        await restarted.CompleteRunAsync(run.RunId, completion, "runner-a", default);
        await restarted.CompleteRunAsync(run.RunId, completion, "runner-a", default);

        var recovered = await restarted.GetTaskAsync(project.ProjectId, task.TaskKey, default);
        Assert.Equal("4-auto-review", recovered!.State);
        var audit = await restarted.ListAuditAsync(0, default);
        Assert.Single(audit, record => record.Action == "result-handoff.acknowledged");
        Assert.Single(audit, record => record.Action == "run.completed");
    }

    [Fact]
    public async Task Restart_accepts_unacknowledged_handoff_from_the_exact_process_unknown_authority()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        await SeedReadyTaskAsync(first);
        await first.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await first.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            restarted.AcknowledgeResultHandoffAsync(
                claim.Run!.RunId,
                Handoff(claim.Run.RunId, claim.Lease! with { Fence = claim.Lease.Fence + 1 }, sequence: 8),
                "runner-a",
                default));
        Assert.Equal("stale-fence", stale.Code);
        var ack = await restarted.AcknowledgeResultHandoffAsync(
            claim.Run!.RunId,
            Handoff(claim.Run.RunId, claim.Lease!, sequence: 7),
            "runner-a",
            default);

        Assert.Equal("acknowledged", ack.State);
    }

    [Fact]
    public async Task Successful_completion_requires_the_matching_durable_envelope()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);

        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() => store.CompleteRunAsync(
            claim.Run!.RunId,
            new CompleteRunRequest(
                "runner-a", "instance-a", claim.Lease!.LeaseId, claim.Lease.Fence, "success",
                ResultEnvelopeDigest: new string('a', 64),
                IdempotencyKey: $"completion:{claim.Run.RunId}",
                Sequence: 2),
            "runner-a",
            default));

        Assert.Equal("result-handoff-required", conflict.Code);
    }

    [Fact]
    public async Task Outbox_observability_reports_backlog_oldest_sequence_and_final_state()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        await store.ReportRunnerOutboxAsync(
            "runner-a",
            new RunnerOutboxStatusRequest(
                "instance-a", 12, 8, 4, 9, "transfer-recovery", "run-1", null, DateTime.UtcNow),
            "runner-a",
            default);

        var rows = await store.ListRunnerOutboxesAsync(default);
        var row = Assert.Single(rows);
        Assert.Equal(4, row.BacklogCount);
        Assert.Equal(9, row.OldestUnacknowledgedSequence);
        Assert.Equal("transfer-recovery", row.FinalHandoffState);
        Assert.Equal(4, store.Status().OutboxBacklog);
        Assert.Equal(9, store.Status().OldestUnacknowledgedSequence);
    }

    [Fact]
    public async Task Outbox_sequence_is_persisted_and_rejects_a_late_new_fact()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync("runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(new ClaimRequest("runner-a", "instance-a"), "test", default);
        var run = claim.Run!;
        var lease = claim.Lease!;
        await store.IngestEventAsync(
            run.RunId,
            new EventIngestRequest(
                "event-2",
                "runner.status",
                "{}",
                "event-key-2",
                lease.Fence,
                RunnerId: lease.RunnerId,
                InstanceId: lease.InstanceId,
                LeaseId: lease.LeaseId,
                Sequence: 2),
            "runner-a",
            default);

        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.IngestEventAsync(
                run.RunId,
                new EventIngestRequest(
                    "event-1",
                    "runner.status",
                    "{}",
                    "event-key-1",
                    lease.Fence,
                    RunnerId: lease.RunnerId,
                    InstanceId: lease.InstanceId,
                    LeaseId: lease.LeaseId,
                    Sequence: 1),
                "runner-a",
                default));

        Assert.Equal("stale-outbox-sequence", stale.Code);
        var stored = Assert.Single(await store.ListEventsAsync(run.RunId, 0, default));
        Assert.Equal(2, stored.Sequence);
    }

    [Fact]
    public async Task Completed_task_extends_result_retention_from_the_terminal_transition()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock, resultRetentionDays: 30);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync(
            "runner-a",
            Runner("instance-a"),
            "test",
            default);
        var claim = await store.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "test",
            default);
        var request = Handoff(
            claim.Run!.RunId,
            claim.Lease!,
            sequence: 1);
        var acknowledgement = await store.AcknowledgeResultHandoffAsync(
            claim.Run.RunId,
            request,
            "runner-a",
            default);
        Assert.Equal(
            clock.GetUtcNow().AddDays(30).UtcDateTime,
            acknowledgement.RetainUntil);

        clock.Advance(TimeSpan.FromDays(10));
        var current = await store.GetTaskAsync(
            project.ProjectId,
            task.TaskKey,
            default);
        await store.UpdateTaskAsync(
            project.ProjectId,
            task.TaskKey,
            new UpdateTaskRequest(
                null,
                null,
                "6-completed",
                current!.Version),
            "test",
            default);

        var retained = await store.GetResultHandoffAsync(
            claim.Run.RunId,
            default);
        Assert.Equal(
            clock.GetUtcNow().AddDays(30).UtcDateTime,
            retained!.RetainUntil);
    }

    [Fact]
    public async Task Invariant_reconciliation_schedules_orphan_process_termination()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 0, 40, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await store.RegisterRunnerAsync(
            "runner-a", Runner("instance-a"), "test", default);

        var started = clock.GetUtcNow().UtcDateTime;
        clock.Advance(TimeSpan.FromSeconds(121));
        await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "instance-a",
                AvailableSlots: 0,
                Inventory: new RunnerProcessInventory(
                    clock.GetUtcNow().UtcDateTime,
                    [new RunnerProcessInfo(
                        "run-without-lease", "TS-404", 4242,
                        "/worktrees/TS-404", started)])),
            "runner-a",
            default);

        Assert.Equal(1, await store.ReconcileInvariantsAsync(default));
        var response = await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "instance-a",
                AvailableSlots: 0,
                Inventory: new RunnerProcessInventory(
                    clock.GetUtcNow().UtcDateTime,
                    [new RunnerProcessInfo(
                        "run-without-lease", "TS-404", 4242,
                        "/worktrees/TS-404", started)])),
            "runner-a",
            default);

        var action = Assert.Single(response.ReconciliationActions!);
        Assert.Equal("run-inventory", action.Category);
        Assert.Equal("terminate-process", action.Action);
        Assert.Equal(4242, action.Pid);
        Assert.Contains(
            await store.ListAuditAsync(0, default),
            record => record.Action == "invariant.orphan-process");
        var registry = await store.GetInvariantRegistryAsync(default);
        Assert.Equal(4, registry.Definitions.Count);
        Assert.Equal(1, registry.PendingRunnerActions);
        Assert.Contains(
            registry.RecentViolations,
            record => record.Action == "invariant.orphan-process");
    }

    [Fact]
    public async Task Invariant_reconciliation_records_lease_without_process_for_backend_authority()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 1, 0, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(store);
        await store.RegisterRunnerAsync(
            "runner-a", Runner("instance-a"), "test", default);
        var claim = await store.ClaimAsync(
            new ClaimRequest(
                "runner-a",
                "instance-a",
                RequestedTtlSeconds: 180),
            "runner-a",
            default);

        clock.Advance(TimeSpan.FromSeconds(121));
        await store.RenewLeaseAsync(
            claim.Run!.RunId,
            new LeaseRenewRequest(
                "runner-a",
                "instance-a",
                claim.Lease!.LeaseId,
                claim.Lease.Fence,
                Inventory: new RunnerProcessInventory(
                    clock.GetUtcNow().UtcDateTime,
                    [])),
            "runner-a",
            default);

        Assert.Equal(1, await store.ReconcileInvariantsAsync(default));
        var retained = await store.GetTaskAsync(
            project.ProjectId, task.TaskKey, default);
        Assert.Equal("3-progress", retained!.State);
        var events = await store.ListEventsAsync(claim.Run.RunId, 0, default);
        var mismatch = Assert.Single(
            events,
            item => item.Kind == "invariant.lease-without-process");
        Assert.Contains("backend-authority", mismatch.PayloadJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invariant_reconciliation_records_progress_without_run_without_requeueing()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 1, 20, 0, TimeSpan.Zero));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(
            new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(
            new CreateProjectRequest(
                workspace.WorkspaceId, "Project", "TS"),
            "test",
            default);
        var task = await store.CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest(
                "Stranded", "No run owns this card", "3-progress"),
            "test",
            default);

        clock.Advance(TimeSpan.FromSeconds(121));
        Assert.Equal(1, await store.ReconcileInvariantsAsync(default));

        var retained = await store.GetTaskAsync(
            project.ProjectId, task.TaskKey, default);
        Assert.Equal("3-progress", retained!.State);
        var audit = await store.ListAuditAsync(0, default);
        Assert.Contains(
            audit,
            record => record.Action == "invariant.lane-process-consistency");
    }

    [Fact]
    public async Task Lane_reconciliation_retains_process_unknown_authority()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(
            new DateTimeOffset(2026, 7, 25, 1, 40, 0, TimeSpan.Zero));
        var first = Store(temp.Path, clock);
        await first.InitializeAsync();
        var (_, project, task) = await SeedReadyTaskAsync(first);
        await first.RegisterRunnerAsync(
            "runner-a", Runner("instance-a"), "test", default);
        await first.ClaimAsync(
            new ClaimRequest("runner-a", "instance-a"),
            "runner-a",
            default);

        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        clock.Advance(TimeSpan.FromSeconds(121));

        Assert.Equal(1, await restarted.ReconcileInvariantsAsync(default));
        var contained = await restarted.GetTaskAsync(
            project.ProjectId, task.TaskKey, default);
        Assert.Equal("3-progress", contained!.State);
        var audit = await restarted.ListAuditAsync(0, default);
        Assert.Contains(
            audit,
            record => record.Action == "invariant.lane-process-consistency"
                      && record.DetailJson.Contains(
                          "containment-required",
                          StringComparison.Ordinal));
        Assert.Equal(0, await restarted.ReconcileInvariantsAsync(default));
    }

    private static ResultHandoffRequest Handoff(string runId, LeaseDto lease, long sequence)
    {
        var envelope = new ImmutableResultEnvelope(
            "repo-project",
            runId,
            new string('1', 40),
            new string('2', 40),
            $"refs/heads/agent-studio/results/{runId}/{new string('2', 40)}",
            null,
            new string('3', 64));
        var digest = ResultEnvelopeDigest.Compute(envelope);
        return new ResultHandoffRequest(
            lease.RunnerId,
            lease.InstanceId,
            lease.LeaseId,
            lease.Fence,
            sequence,
            $"handoff:{runId}:{digest}",
            digest,
            envelope);
    }

    private static TaskServerStore Store(
        string dataDirectory,
        TimeProvider? clock = null,
        int resultRetentionDays = 30)
        => new(
            Options.Create(new TaskServerOptions
            {
                DataDirectory = dataDirectory,
                ResultRetentionDays = resultRetentionDays,
            }),
            clock ?? TimeProvider.System);

    private static RegisterRunnerRequest Runner(string instance)
        => new(
            "runner",
            "host-a",
            instance,
            "1.0.0",
            TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]);

    private static void AssertCanOpenExclusively(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.True(stream.CanRead);
        Assert.True(stream.CanWrite);
    }

    private static async Task<(WorkspaceDto Workspace, ProjectDto Project, TaskDto Task)> SeedReadyTaskAsync(TaskServerStore store)
    {
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Project", "TS"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Task", "Do the work", "2-ready"), "test", default);
        return (workspace, project, task);
    }
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan duration) => _utcNow += duration;
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory() => Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "task-server-tests", Guid.NewGuid().ToString("N"));
    public string Path { get; }

    public static async Task RunAsync(Func<TempDirectory, Task> action)
    {
        var temp = new TempDirectory();
        Exception? failure = null;
        try
        {
            await action(temp);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        try
        {
            temp.Dispose();
        }
        catch (Exception cleanupException)
        {
            failure = failure is null
                ? cleanupException
                : new AggregateException(
                    "The test body and temporary-directory cleanup both failed.",
                    failure,
                    cleanupException);
        }

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    public void Dispose()
    {
        if (!Directory.Exists(Path)) return;
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new IOException($"Failed to clean temporary test directory '{Path}'.", exception);
        }
    }
}
