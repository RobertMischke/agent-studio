using OrchestratorApi.Models;
using OrchestratorApi.Services;
using OrchestratorApi.Services.Tasks;
using System.Text;

namespace OrchestratorApi.Endpoints.Tasks;

/// <summary>
/// Read/write of files inside a job folder: <c>prompt.md</c> /
/// <c>status.md</c> via <c>/files/{name}</c>, plus the
/// <c>attachments/</c> upload/download surface used by the prompt
/// editor and the read-only <c>results/</c> mirror that backs
/// <c>status.md</c> image references. See
/// <c>docs/protocol-style.md</c> for the storage contract.
/// </summary>
public static class TaskFilesEndpoints
{
    public static void MapTaskFilesEndpoints(this RouteGroupBuilder group)
    {
        group.MapGet("/{jobId}/files/{fileName}", (string jobId, string fileName, string? watchPath, TaskScannerService scanner) =>
        {
            var content = scanner.ReadJobFile(jobId, fileName, watchPath);
            return content is null ? Results.NotFound() : JobTextFile(fileName, content);
        });

        // Lists every `.md` file directly in the job root (status.md excluded).
        // Drives the detail view's Files tab (F48): prompt + aspect verdicts +
        // operator notes surface as a sortable, kind-classified manifest, with
        // content fetched lazily through `/files/{fileName}`.
        group.MapGet("/{jobId}/artifacts", (string jobId, string? watchPath, TaskScannerService scanner) =>
        {
            var response = scanner.ListArtifacts(jobId, watchPath);
            return response is null ? Results.NotFound() : Results.Ok(response);
        });

        group.MapPut("/{jobId}/files/{fileName}", (string jobId, string fileName, string? watchPath, UpdateJobFileRequest req, TaskMutationService mutations, TaskRunnerService runner) =>
        {
            if (runner.IsJobLive(jobId, watchPath))
                return Results.Conflict("Cannot edit while the CLI is running for this task — stop it first.");

            try
            {
                var success = mutations.UpdateJobFile(jobId, fileName, req.Content, watchPath);
                return success ? Results.Ok() : Results.NotFound("Job not found or file is not editable.");
            }
            catch (IOException ex)
            {
                // File was locked by another process (editor, indexer, AV) for longer than
                // the retry window. Surface a tidy 503 instead of a stack-trace modal.
                return Results.Json(
                    new { error = "File is temporarily locked by another process — try saving again in a moment.", detail = ex.Message },
                    statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        });

        // Prompt-editor screenshot uploads — written to <job>/attachments/<id>.<ext> and
        // referenced from prompt.md as a relative path so the CLI agent finds them on disk.
        group.MapPost("/{jobId}/attachments", async (string jobId, string? watchPath, HttpRequest request, TaskMutationService mutations) =>
        {
            if (!request.HasFormContentType)
                return Results.BadRequest(new { error = "multipart/form-data expected" });

            var form = await request.ReadFormAsync();
            var file = form.Files["file"] ?? form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
                return Results.BadRequest(new { error = "No file uploaded" });

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms);

            var (fileName, error) = mutations.SaveAttachment(jobId, watchPath, ms.ToArray(), file.FileName, file.ContentType);
            if (fileName is null) return Results.BadRequest(new { error });

            // Relative URL so the editor renders it via the API; markdown stores `attachments/<file>`.
            var watchPathQuery = string.IsNullOrEmpty(watchPath) ? "" : $"?watchPath={Uri.EscapeDataString(watchPath)}";
            return Results.Ok(new
            {
                fileName,
                relativePath = $"attachments/{fileName}",
                url = $"/api/tasks/{Uri.EscapeDataString(jobId)}/attachments/{fileName}{watchPathQuery}"
            });
        }).DisableAntiforgery();

        group.MapGet("/{jobId}/attachments/{fileName}", (string jobId, string fileName, string? watchPath, TaskScannerService scanner) =>
        {
            var (path, contentType) = scanner.ResolveAttachment(jobId, fileName, watchPath);
            return path is null ? Results.NotFound() : Results.File(path, contentType);
        });

        // Read-only mirror of /attachments/ for the job's `results/` folder — the
        // place where agents drop screenshots that should survive past the next
        // Playwright run. The protocol pane resolves `results/<name>` references
        // in status.md against this URL. See docs/protocol-style.md.
        group.MapGet("/{jobId}/results/{fileName}", (string jobId, string fileName, string? watchPath, TaskScannerService scanner) =>
        {
            var (path, contentType) = scanner.ResolveResult(jobId, fileName, watchPath);
            return path is null ? Results.NotFound() : Results.File(path, contentType);
        });

        // Ordered listing of every image under <job>/results/ (recursive),
        // captioned per spec/folder, with pass-fail status pulled from the
        // Playwright harvest index when available. Drives the protocol-pane
        // screenshot strip and the lightbox prev/next navigation.
        group.MapGet("/{jobId}/screenshots", (string jobId, string? watchPath, ScreenshotIndexService screenshots) =>
        {
            var entries = screenshots.ListJobScreenshots(jobId, watchPath);
            return Results.Ok(new TaskScreenshotsResponse
            {
                JobId = jobId,
                Screenshots = entries.ToList()
            });
        });

        // Sub-path aware companion to /results/{fileName}. The flat endpoint
        // above rejects path separators by design (see TaskScannerService);
        // the screenshot listing returns nested paths under
        // results/playwright/<spec>/... which need this dedicated server.
        // Path traversal is rejected inside ResolveScreenshotFile.
        group.MapGet("/{jobId}/screenshot", (string jobId, string? path, string? watchPath, ScreenshotIndexService screenshots) =>
        {
            if (string.IsNullOrWhiteSpace(path)) return Results.BadRequest(new { error = "path is required" });
            var (resolved, contentType) = screenshots.ResolveScreenshotFile(jobId, path, watchPath);
            return resolved is null ? Results.NotFound() : Results.File(resolved, contentType);
        });
    }

    private static IResult JobTextFile(string fileName, string content)
    {
        var contentType = fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
            ? "application/json"
            : fileName.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                ? "text/markdown"
                : "text/plain";
        return Results.Text(content, contentType, Encoding.UTF8);
    }
}
