using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Runner;

/// <summary>
/// Append-only evidence writer for the orchestrator-owned post-processing
/// phase. The log is intentionally independent from the lane move: it records
/// what the post-processing identity decided, while the state machine remains
/// the only authority for folder transitions.
/// </summary>
public static class PostProcessingOutcomeLog
{
    public const string FileName = "post-processing-outcomes.jsonl";

    private static readonly JsonSerializerOptions WriteOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Append(string jobFolderPath, PostProcessingOutcomeRecord record, ILogger logger)
    {
        try
        {
            Directory.CreateDirectory(jobFolderPath);
            var path = Path.Combine(jobFolderPath, FileName);
            File.AppendAllText(path, JsonSerializer.Serialize(record, WriteOpts) + Environment.NewLine);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to append post-processing outcome for {JobId}", record.JobId);
        }
    }
}
