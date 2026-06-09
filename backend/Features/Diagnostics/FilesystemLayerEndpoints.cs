using OrchestratorApi.Services;

namespace OrchestratorApi.Endpoints;

public static class FilesystemLayerEndpoints
{
    public static void MapFilesystemLayerEndpoints(this WebApplication app)
    {
        app.MapGet("/api/filesystem-layer/snapshot",
            (FilesystemLayerSnapshotService snapshots, string? rootPath, bool refresh) =>
            {
                try
                {
                    return Results.Ok(snapshots.GetSnapshot(rootPath, refresh));
                }
                catch (DirectoryNotFoundException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            });
    }
}
