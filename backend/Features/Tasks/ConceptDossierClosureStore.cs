using System.Text.Json;

namespace AgentStudio.Tasks;

public sealed record ConceptDossierClosureRecord
{
    public bool NoDossierNeeded { get; init; }
    public string? Reason { get; init; }
    public DateTime DeclaredAt { get; init; }
}

/// <summary>Durable operator explanation for a concept that needs no dossier.</summary>
public static class ConceptDossierClosureStore
{
    private const string FileName = "concept-dossier-closure.json";
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    public static ConceptDossierClosureRecord? Read(string taskFolder)
    {
        try
        {
            var path = Path.Combine(taskFolder, ".metadata", FileName);
            return File.Exists(path)
                ? JsonSerializer.Deserialize<ConceptDossierClosureRecord>(File.ReadAllText(path), Options)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public static bool Write(string taskFolder, string reason, ILogger? logger = null)
    {
        try
        {
            var directory = Path.Combine(taskFolder, ".metadata");
            Directory.CreateDirectory(directory);
            var record = new ConceptDossierClosureRecord
            {
                NoDossierNeeded = true,
                Reason = reason.Trim(),
                DeclaredAt = DateTime.UtcNow,
            };
            File.WriteAllText(
                Path.Combine(directory, FileName),
                JsonSerializer.Serialize(record, Options) + Environment.NewLine);
            return true;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not persist concept dossier closure for {Folder}", taskFolder);
            return false;
        }
    }

    public static void Clear(string taskFolder, ILogger? logger = null)
    {
        try
        {
            var path = Path.Combine(taskFolder, ".metadata", FileName);
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not clear concept dossier closure for {Folder}", taskFolder);
        }
    }
}
