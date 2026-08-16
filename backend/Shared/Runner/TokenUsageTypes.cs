namespace AgentStudio.Shared;

/// <summary>Stable wire keys for task token usage grouped by pipeline purpose.</summary>
public static class TokenUsageTypes
{
    public const string Coding = "coding";
    public const string Review = "review";
    public const string Gate = "gate";
    public const string Enrichment = "enrichment";
    public const string Orchestration = "orchestration";
    public const string Supporting = "supporting";
    public const string Other = "other";
}
