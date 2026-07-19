using System.Text.Json.Serialization;

namespace AgentStudio.Shared;

public static class WikiTaskReferenceSources
{
    public const string Auto = "auto";
    public const string Manual = "manual";
}

public record RelatedWikiPage
{
    [JsonPropertyName("relPath")]
    public string RelPath { get; init; } = "";
    [JsonPropertyName("title")]
    public string Title { get; init; } = "";
    [JsonPropertyName("linkedAt")]
    public DateTime LinkedAt { get; init; }
    [JsonPropertyName("source")]
    public string Source { get; init; } = WikiTaskReferenceSources.Auto;
    [JsonPropertyName("exists")]
    public bool? Exists { get; init; }
}

public record RelatedTask
{
    [JsonPropertyName("key")]
    public string Key { get; init; } = "";
    [JsonPropertyName("title")]
    public string Title { get; init; } = "";
    [JsonPropertyName("linkedAt")]
    public DateTime LinkedAt { get; init; }
    [JsonPropertyName("source")]
    public string Source { get; init; } = WikiTaskReferenceSources.Auto;
    [JsonPropertyName("exists")]
    public bool? Exists { get; init; }
}
