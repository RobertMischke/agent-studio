using System.Text.Json;
using OrchestratorApi.Models;
using OrchestratorApi.Services.Analysis;
using OrchestratorApi.Services.Supervisor;
using Xunit;

namespace OrchestratorApi.Tests;

/// <summary>
/// Locks the round-trip between the JSON schemas under
/// <c>docs/schemas/</c> and the C# records that flow through
/// <c>OrchestratorApi.Services.State</c>: every required field listed in the
/// schema appears as a serialised property on a fresh canonical example, and
/// every documented enum value parses back into its C# enum without loss.
/// </summary>
/// <remarks>
/// We do not pull in a full JSON-Schema validator here. The repository's
/// validation policy is "in-code, mirroring the schema" (see
/// <c>backend/Services/Bus/AgentMessageValidator.cs</c> and
/// <c>backend/Services/State/SupervisorRecordValidator.cs</c>). These tests
/// pin the alignment by serialising a canonical record, parsing the schema,
/// and asserting that every <c>required</c> property the schema lists is
/// present in the serialised JSON.
/// </remarks>
public class SchemaRoundTripTests
{
    private static readonly JsonSerializerOptions WebOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void SupervisorAdvisory_CanonicalExample_ContainsEverySchemaRequiredProperty()
    {
        var advisory = new SupervisorAdvisory(
            CreatedAt: new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
            Project: "agent-taskboard",
            Severity: SupervisorSeverity.High,
            Source: SupervisorSource.HardCheck,
            Topic: "no-progress",
            Message: "No log line in 12 minutes while job is running.",
            JobId: "refactor-job-service");

        var json = JsonSerializer.SerializeToElement(advisory, WebOptions);
        AssertEveryRequiredPropertyPresent(LoadSchema("supervisor-advisory.schema.json"), json);
    }

    [Fact]
    public void SupervisorIntervention_CanonicalExample_ContainsEverySchemaRequiredProperty()
    {
        var intervention = new SupervisorIntervention(
            CreatedAt: new DateTime(2026, 5, 5, 10, 1, 0, DateTimeKind.Utc),
            Project: "agent-taskboard",
            Kind: SupervisorInterventionKind.PausePickup,
            Source: SupervisorSource.AutoIntervention,
            Reason: "tool-call repeat threshold reached",
            JobId: null,
            PauseTtl: TimeSpan.FromMinutes(30));

        var json = JsonSerializer.SerializeToElement(intervention, WebOptions);
        AssertEveryRequiredPropertyPresent(LoadSchema("supervisor-intervention.schema.json"), json);
    }

    [Fact]
    public void TokenAggregateSchema_PublishesPerCliBudgetWindowShape()
    {
        // No C# record yet for token-aggregate (the in-memory consumer lands
        // with the token-spend timeline work). Lock the schema's published
        // shape so a future refactor cannot quietly drop a required field.
        var schema = LoadSchema("token-aggregate.schema.json");
        var required = ReadStringArray(schema, "required");

        Assert.Contains("project", required);
        Assert.Contains("windowStart", required);
        Assert.Contains("windowEnd", required);
        Assert.Contains("cli", required);
        Assert.Contains("model", required);
        Assert.Contains("tokens", required);

        var properties = schema.GetProperty("properties");
        var cliEnum = ReadStringArray(properties.GetProperty("cli"), "enum");
        Assert.Contains("claude", cliEnum);
        Assert.Contains("codex", cliEnum);
        Assert.Contains("copilot", cliEnum);
        Assert.Contains("gemini", cliEnum);

        var tokens = properties.GetProperty("tokens");
        var tokensRequired = ReadStringArray(tokens, "required");
        Assert.Contains("input", tokensRequired);
        Assert.Contains("output", tokensRequired);
    }

    [Fact]
    public void SupervisorAdvisorySchema_ListsTheFourSourcesAndThreeSeverities()
    {
        var schema = LoadSchema("supervisor-advisory.schema.json");
        var properties = schema.GetProperty("properties");

        var severities = ReadStringArray(properties.GetProperty("severity"), "enum");
        Assert.Equal(new[] { "Info", "Warn", "High" }, severities);

        var sources = ReadStringArray(properties.GetProperty("source"), "enum");
        Assert.Equal(new[] { "HardCheck", "SoftReasoning", "User", "AutoIntervention" }, sources);

        // Every source spelled in the schema must round-trip through the C# enum.
        foreach (var name in sources)
        {
            Assert.True(Enum.TryParse<SupervisorSource>(name, ignoreCase: false, out _),
                $"Schema source '{name}' has no matching C# enum value.");
        }
        foreach (var name in severities)
        {
            Assert.True(Enum.TryParse<SupervisorSeverity>(name, ignoreCase: false, out _),
                $"Schema severity '{name}' has no matching C# enum value.");
        }
    }

    [Fact]
    public void ClientIdentitySchema_PublishesTheRegistrationBoundaryShape()
    {
        var schema = LoadSchema("client-identity.schema.json");
        var required = ReadStringArray(schema, "required");
        Assert.Contains("id", required);
        Assert.Contains("displayName", required);
        Assert.Contains("kind", required);
        Assert.Contains("registeredAt", required);

        var properties = schema.GetProperty("properties");
        var kinds = ReadStringArray(properties.GetProperty("kind"), "enum");
        Assert.Equal(new[] { "human", "agent-instance", "external-tool", "service", "retired" }, kinds);

        // Every documented kind round-trips through the C# parser.
        Assert.Equal(ClientIdentityKind.Human, ClientIdentityKinds.Parse("human"));
        Assert.Equal(ClientIdentityKind.AgentInstance, ClientIdentityKinds.Parse("agent-instance"));
        Assert.Equal(ClientIdentityKind.ExternalTool, ClientIdentityKinds.Parse("external-tool"));
        Assert.Equal(ClientIdentityKind.Service, ClientIdentityKinds.Parse("service"));
        Assert.Equal(ClientIdentityKind.Retired, ClientIdentityKinds.Parse("retired"));
    }

    [Fact]
    public void TokenAggregateByClientSchema_KeysOnClientIdInAdditionToProject()
    {
        var schema = LoadSchema("token-aggregate-by-client.schema.json");
        var required = ReadStringArray(schema, "required");
        Assert.Contains("clientId", required);
        Assert.Contains("project", required);
        Assert.Contains("windowStart", required);
        Assert.Contains("windowEnd", required);
        Assert.Contains("cli", required);
        Assert.Contains("model", required);
        Assert.Contains("tokens", required);
    }

    [Fact]
    public void AnalysisReport_CanonicalExample_ContainsEverySchemaRequiredProperty()
    {
        var report = new AnalysisReport(
            ReportId: "01HX0000000000000000000001",
            CreatedAt: new DateTime(2026, 5, 5, 10, 0, 0, DateTimeKind.Utc),
            Scope: new AnalysisReportScope(AnalysisReportScopeKind.Project, Project: "agent-taskboard"),
            Producer: new AnalysisReportProducer(AnalysisReportProducerKind.Manual, Agent: "user"),
            Trigger: AnalysisReportTrigger.Manual,
            Topic: "are-we-on-track",
            Summary: "On track with two follow-up suggestions.",
            Severity: AnalysisReportSeverity.Warn,
            ParseStatus: AnalysisReportParseStatus.Structured,
            References: new[]
            {
                new AnalysisReportReference(AnalysisReportReferenceKind.Job, "agent-taskboard/3-progress/sample"),
            },
            FollowUpTaskSuggestions: new[]
            {
                new AnalysisReportFollowUpTaskSuggestion(
                    Title: "Resync ROADMAP.md with queue",
                    Summary: "Two themes drifted.",
                    Priority: AnalysisReportFollowUpPriority.Normal,
                    RelatedTopic: AnalysisReportFollowUpRelatedTopic.RoadmapAlignment),
            });

        // The on-disk format is what consumers compare against.
        var serializerOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        var json = JsonSerializer.SerializeToElement(report, serializerOptions);
        AssertEveryRequiredPropertyPresent(LoadSchema("analysis-report.schema.json"), json);
    }

    [Fact]
    public void AnalysisReportSchema_ListsTheFiveScopesProducersAndParseStates()
    {
        var schema = LoadSchema("analysis-report.schema.json");
        var properties = schema.GetProperty("properties");

        var scopes = ReadStringArray(properties.GetProperty("scope").GetProperty("properties").GetProperty("kind"), "enum");
        Assert.Equal(new[] { "Workspace", "Project", "Task", "Run", "TimeWindow" }, scopes);
        foreach (var name in scopes)
        {
            Assert.True(Enum.TryParse<AnalysisReportScopeKind>(name, ignoreCase: false, out _),
                $"Schema scope kind '{name}' has no matching C# enum value.");
        }

        var producerKinds = ReadStringArray(
            properties.GetProperty("producer").GetProperty("properties").GetProperty("kind"), "enum");
        Assert.Equal(new[] { "Manual", "Scheduled", "MetaCycle", "SupportingAgent", "ExternalMonitor" }, producerKinds);
        foreach (var name in producerKinds)
        {
            Assert.True(Enum.TryParse<AnalysisReportProducerKind>(name, ignoreCase: false, out _),
                $"Schema producer kind '{name}' has no matching C# enum value.");
        }

        var triggers = ReadStringArray(properties.GetProperty("trigger"), "enum");
        Assert.Equal(new[] { "Manual", "Scheduled", "MetaCycle", "SupportingAgent", "ExternalMonitor" }, triggers);
        foreach (var name in triggers)
        {
            Assert.True(Enum.TryParse<AnalysisReportTrigger>(name, ignoreCase: false, out _),
                $"Schema trigger '{name}' has no matching C# enum value.");
        }

        var severities = ReadStringArray(properties.GetProperty("severity"), "enum");
        Assert.Equal(new[] { "Info", "Warn", "High", "Critical" }, severities);
        foreach (var name in severities)
        {
            Assert.True(Enum.TryParse<AnalysisReportSeverity>(name, ignoreCase: false, out _),
                $"Schema severity '{name}' has no matching C# enum value.");
        }

        var parseStates = ReadStringArray(properties.GetProperty("parseStatus"), "enum");
        Assert.Equal(new[] { "Structured", "Unstructured", "MalformedJson" }, parseStates);
        foreach (var name in parseStates)
        {
            Assert.True(Enum.TryParse<AnalysisReportParseStatus>(name, ignoreCase: false, out _),
                $"Schema parseStatus '{name}' has no matching C# enum value.");
        }

        // Reference kinds in the schema must each round-trip through the C# enum.
        var refKinds = ReadStringArray(
            properties.GetProperty("references")
                .GetProperty("items")
                .GetProperty("properties")
                .GetProperty("kind"),
            "enum");
        Assert.Equal(
            new[] { "Job", "Run", "Commit", "Screenshot", "BusMessage", "RuntimeEvent", "PreviousReport", "LogSlice", "Doc" },
            refKinds);
        foreach (var name in refKinds)
        {
            Assert.True(Enum.TryParse<AnalysisReportReferenceKind>(name, ignoreCase: false, out _),
                $"Schema reference kind '{name}' has no matching C# enum value.");
        }
    }

    [Fact]
    public void SupervisorInterventionSchema_ListsTheFourPreEmptiveKinds()
    {
        var schema = LoadSchema("supervisor-intervention.schema.json");
        var kinds = ReadStringArray(schema.GetProperty("properties").GetProperty("kind"), "enum");
        Assert.Equal(new[] { "CancelRun", "PausePickup", "ForceFail", "Resume" }, kinds);

        foreach (var name in kinds)
        {
            Assert.True(Enum.TryParse<SupervisorInterventionKind>(name, ignoreCase: false, out _),
                $"Schema kind '{name}' has no matching C# enum value.");
        }
    }

    private static JsonElement LoadSchema(string fileName)
    {
        var path = ResolveSchemaPath(fileName);
        Assert.True(File.Exists(path), $"Schema not found at {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.Clone();
    }

    private static string ResolveSchemaPath(string fileName)
    {
        // Walk up from the test binary location to the repo root, then into
        // docs/schemas/. The test runner's working directory is the test
        // project's bin/Debug/net10.0/.
        var current = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && current is not null; i++)
        {
            var candidate = Path.Combine(current, "docs", "schemas", fileName);
            if (File.Exists(candidate)) return candidate;
            current = Directory.GetParent(current)?.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "docs", "schemas", fileName);
    }

    private static string[] ReadStringArray(JsonElement element, string property)
    {
        var array = element.GetProperty(property);
        var list = new List<string>(array.GetArrayLength());
        foreach (var item in array.EnumerateArray()) list.Add(item.GetString() ?? "");
        return list.ToArray();
    }

    private static void AssertEveryRequiredPropertyPresent(JsonElement schema, JsonElement instance)
    {
        var required = ReadStringArray(schema, "required");
        foreach (var name in required)
        {
            Assert.True(instance.TryGetProperty(name, out _),
                $"Required property '{name}' missing from serialised instance.");
        }
    }
}
