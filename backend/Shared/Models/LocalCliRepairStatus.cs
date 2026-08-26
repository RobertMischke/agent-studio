namespace AgentStudio.Shared;

/// <summary>Compact, secret-free host/UI projection of the latest repair event.</summary>
public sealed record LocalCliRepairStatus
{
    public string CliType { get; init; } = "";
    public string Outcome { get; init; } = "";
    public DateTimeOffset OccurredAt { get; init; }
    public string? BeforeVersion { get; init; }
    public string? AfterVersion { get; init; }
    public string? Error { get; init; }
}
