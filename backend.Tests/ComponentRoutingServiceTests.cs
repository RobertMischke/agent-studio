using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class ComponentRoutingServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "component-routing-" + Guid.NewGuid().ToString("N"));
    private readonly ProjectRegistry _registry;
    private readonly ProjectRecord _cac;
    private readonly ProjectRecord _agt;

    public ComponentRoutingServiceTests()
    {
        Directory.CreateDirectory(_root);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
        }).Build();
        _registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var cac = _registry.EnsureProjectForStorage(Path.Combine(_root, "cac"), "Coding Agent Chat", DefaultWorkspace.Id);
        var agt = _registry.EnsureProjectForStorage(Path.Combine(_root, "agt"), "Agent Studio", DefaultWorkspace.Id);
        _cac = _registry.SetShortCode(cac.Id, "CAC");
        _agt = _registry.SetShortCode(agt.Id, "AGT");

        _registry.UpsertOwnershipMapping(_cac.Id, new ComponentOwnershipMapping
        {
            Id = "chat",
            ObservedSurfaces = ["Agent Studio chat message"],
            Component = "Coding Agent Chat rendering footer message components",
            PackageOrModule = "coding-agent-chat",
            PrimaryProjectId = _cac.Id,
            Repository = "coding-agent-chat",
            ConsumerProjectIds = [_agt.Id],
            IntegrationHosts = ["Agent Studio"],
            ReleaseArtifact = "coding-agent-chat npm package",
            VersioningMechanism = "npm package version",
            DeploymentSteps = ["Publish package", "Update Agent Studio dependency", "Deploy Agent Studio"],
            Environments = ["development", "stable"],
            AllowedTicketPrefix = "CAC",
            Evidence = ["frontend/AGENTS.md"],
            Confidence = 1,
        }, "owner@example.test");
        _registry.UpsertOwnershipMapping(_agt.Id, new ComponentOwnershipMapping
        {
            Id = "backend",
            ObservedSurfaces = ["Agent Studio API"],
            Component = "Agent Studio backend API",
            PackageOrModule = "backend",
            PrimaryProjectId = _agt.Id,
            AllowedTicketPrefix = "AGT",
            Evidence = ["backend source ownership"],
            Confidence = 1,
        });
    }

    [Fact]
    public void ChatMessageObservedInAgentStudio_RoutesToCacWithAgtDelivery()
    {
        var result = new ComponentRoutingService(_registry).Resolve(new(
            "Agent Studio Orchestrator chat", "CAC-rendered message", _agt.Id));

        Assert.False(result.RequiresQuestion);
        Assert.Equal("CAC", result.PrimaryProject!.ShortCode);
        Assert.Equal("AGT", Assert.Single(result.ConsumerProjects).ShortCode);
        Assert.Equal("coding-agent-chat npm package", result.ReleaseArtifact);
        Assert.Equal("Create CAC ticket; integrate/deploy in Agent Studio.", result.Preview);
    }

    [Fact]
    public void AgentStudioBackendIssue_StaysInAgt()
    {
        var result = new ComponentRoutingService(_registry).Resolve(new(
            "Agent Studio API", "Agent Studio backend API", _agt.Id));

        Assert.False(result.RequiresQuestion);
        Assert.Equal("AGT", result.PrimaryProject!.ShortCode);
        Assert.Empty(result.ConsumerProjects);
    }

    [Fact]
    public void CrossProjectRoute_AppendsConsumerReleaseAndDeploymentAcceptance()
    {
        var route = new ComponentRoutingService(_registry).Resolve(new(
            "Agent Studio chat", "CAC-rendered message", _agt.Id));

        var prompt = TaskCrudEndpoints.AppendDeliveryAcceptanceCriteria("## Problem\nFix rendering.", route)!;

        Assert.Contains("PROJ-001 (CAC)", prompt);
        Assert.Contains("PROJ-002 (AGT)", prompt);
        Assert.Contains("Publish package", prompt);
        Assert.Contains("development, stable", prompt);
        Assert.Contains("mapping chat v1", prompt);
    }

    [Fact]
    public void UnknownOwnership_AsksInsteadOfFallingBackToNavigationProject()
    {
        var result = new ComponentRoutingService(_registry).Resolve(new(
            "Agent Studio unknown screen", "unregistered shared widget", _agt.Id));

        Assert.True(result.RequiresQuestion);
        Assert.Null(result.PrimaryProject);
        Assert.Equal("AGT", result.NavigationProject!.ShortCode);
    }

    [Fact]
    public void ConflictingMappings_AskInsteadOfChoosingEitherOwner()
    {
        var other = _registry.EnsureProjectForStorage(Path.Combine(_root, "other"), "Other Library", DefaultWorkspace.Id);
        _registry.SetShortCode(other.Id, "OTH");
        _registry.UpsertOwnershipMapping(other.Id, new ComponentOwnershipMapping
        {
            Id = "other-chat", Component = "Coding Agent Chat rendering footer message components",
            PackageOrModule = "coding-agent-chat", PrimaryProjectId = other.Id,
            AllowedTicketPrefix = "OTH", Confidence = 1,
        });

        var result = new ComponentRoutingService(_registry).Resolve(new(
            "Agent Studio chat", "CAC-rendered message", _agt.Id));

        Assert.True(result.RequiresQuestion);
        Assert.Null(result.PrimaryProject);
        Assert.Contains("conflict", result.QuestionReason!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MappingEdit_IncrementsVersionAndAppendsAudit()
    {
        var original = _registry.FindById(_cac.Id)!.OwnershipMappings.Single(row => row.Id == "chat");
        var updated = _registry.UpsertOwnershipMapping(_cac.Id, original with { Evidence = ["updated evidence"] }, "project-owner");

        Assert.Equal(2, updated.OwnershipMappings.Single(row => row.Id == "chat").Version);
        Assert.Equal(2, updated.OwnershipMappingAudit.Count(row => row.MappingId == "chat"));
        Assert.Equal("project-owner", updated.OwnershipMappingAudit.Last(row => row.MappingId == "chat").ChangedBy);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }
}
