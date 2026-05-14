using System.Text.Json;
using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Jobs;

/// <summary>
/// Reads and appends <c>title-history.json</c> in a job folder.
/// The file is a JSON array of <see cref="JobTitleHistoryEntry"/> records,
/// oldest first. Append is best-effort: a malformed or unreadable file
/// is treated as empty so a rename never fails on a corrupt history
/// sidecar.
/// </summary>
internal static class TitleHistoryLog
{
    public const string FileName = "title-history.json";

    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    public static List<JobTitleHistoryEntry> Read(string jobFolder)
    {
        var path = Path.Combine(jobFolder, FileName);
        if (!File.Exists(path)) return [];
        try
        {
            var json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return [];
            var entries = JsonSerializer.Deserialize<List<JobTitleHistoryEntry>>(json, JobJsonFile.ReadOpts);
            return entries ?? [];
        }
        catch
        {
            return [];
        }
    }

    public static void Append(string jobFolder, JobTitleHistoryEntry entry, ILogger logger)
    {
        try
        {
            var entries = Read(jobFolder);
            entries.Add(entry);
            var path = Path.Combine(jobFolder, FileName);
            File.WriteAllText(path, JsonSerializer.Serialize(entries, WriteOpts));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to append title history entry in {Dir}", jobFolder);
        }
    }
}
