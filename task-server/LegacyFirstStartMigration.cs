using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

public static class LegacyFirstStartMigration
{
    public static async Task ExecuteAsync(
        IConfiguration configuration,
        TaskServerStore store,
        LegacyMigrationService migration,
        CancellationToken ct)
    {
        var legacyRoot = configuration["LEGACY_MIGRATION_ROOT"]?.Trim();
        if (string.IsNullOrWhiteSpace(legacyRoot)) return;
        if (!configuration.GetValue<bool>("LEGACY_MIGRATION_FREEZE_CONFIRMED"))
            throw new InvalidOperationException(
                "LEGACY_MIGRATION_ROOT requires LEGACY_MIGRATION_FREEZE_CONFIRMED=true after every legacy writer is stopped.");

        var request = new LegacyMigrationRequest(
            legacyRoot,
            configuration["LEGACY_MIGRATION_WORKSPACE"]?.Trim() ?? "Agent Studio",
            FreezeConfirmed: false,
            PreserveEvidenceGit: true);
        var inventory = await migration.InventoryAsync(request, ct);
        var importedMigration = await store.ReadLegacyMigrationIdAsync(ct);
        if (importedMigration is null)
        {
            if (!await store.IsLegacyImportTargetEmptyAsync(ct))
                throw new InvalidOperationException(
                    "Legacy first-start migration requires an empty Task Server authority store.");
            await store.ChangeModeAsync(
                new ChangeModeRequest(TaskServerMode.Maintenance, "planned legacy single-writer cutover"),
                "task-server-first-start",
                ct);
            await migration.ImportAsync(
                request with { FreezeConfirmed = true, ExpectedMigrationId = inventory.MigrationId },
                "task-server-first-start",
                ct);
            await store.ChangeModeAsync(
                new ChangeModeRequest(TaskServerMode.Normal, "legacy authority import verified"),
                "task-server-first-start",
                ct);
        }
        else if (!string.Equals(importedMigration, inventory.MigrationId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Legacy source changed after migration '{importedMigration}'; refusing authority fork '{inventory.MigrationId}'.");
        }
    }
}
