namespace AgentStudio.Shared;

/// <summary>
/// Read-time concept-delivery projection. Until structured
/// <c>references.workbenches</c> ships, the repository-relative path is
/// detected from <c>results/deliverables.md</c> and <c>status.md</c>.
/// </summary>
public sealed record ConceptDossierSummary
{
    public string? RepoRelativePath { get; init; }
    public string? ReferenceSource { get; init; }
    public bool NoDossierNeeded { get; init; }
    public string? NoDossierReason { get; init; }
    public DateTime? DeclaredAt { get; init; }
    public bool ContractSatisfied =>
        !string.IsNullOrWhiteSpace(RepoRelativePath) || NoDossierNeeded;
}

/// <summary>Operator correction for a concept card's dossier contract.</summary>
public sealed record SetConceptDossierRequest
{
    public string? Path { get; init; }
    public bool NoDossierNeeded { get; init; }
    public string? Reason { get; init; }
}
