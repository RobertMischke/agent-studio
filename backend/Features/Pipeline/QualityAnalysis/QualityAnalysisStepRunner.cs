using System.Collections;
using System.Reflection;
using System.Text.Json;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Pipeline;

public static class QualityAnalysisPolicyFiles
{
    public const string RelativePath = ".quality/agent-studio.json";
    public const string SchemaId = "https://agent.studio/schemas/quality-analysis-policy.v1.schema.json";
}

public sealed record QualityAnalysisSelection(
    IReadOnlySet<string> EnabledSteps,
    string? ConfigurationPath,
    IReadOnlyList<string> AngularPaths,
    IReadOnlyList<string> DotNetPaths);

/// <summary>
/// Pure card-class policy. Changed repository paths define the card class;
/// project policy may only come from the versioned repository file. There is
/// deliberately no card, appsettings, or environment override.
/// </summary>
public static class QualityAnalysisPolicy
{
    public static QualityAnalysisSelection Resolve(
        string repositoryPath,
        IReadOnlyList<string>? changedFiles)
    {
        var paths = (changedFiles ?? [])
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var angularPaths = paths.Where(path => IsAngularPath(repositoryPath, path)).ToArray();
        var dotNetPaths = paths.Where(IsDotNetPath).ToArray();
        var enabled = new HashSet<string>(StringComparer.Ordinal)
        {
            // Every classified project receives its stack's core rules by
            // convention. Axis additions are explicit below.
        };

        if (angularPaths.Length > 0)
        {
            enabled.Add(PipelineCatalogue.QualityAngularRulesStepId);
            enabled.Add(PipelineCatalogue.QualityVisualStepId);
        }
        if (dotNetPaths.Length > 0)
        {
            enabled.Add(PipelineCatalogue.QualityDotNetRulesStepId);
            enabled.Add(PipelineCatalogue.QualitySecurityStepId);
        }

        var configurationPath = ApplyRepositoryOverrides(repositoryPath, enabled);
        return new QualityAnalysisSelection(enabled, configurationPath, angularPaths, dotNetPaths);
    }

    private static string? ApplyRepositoryOverrides(string repositoryPath, HashSet<string> enabled)
    {
        var path = Path.Combine(repositoryPath,
            QualityAnalysisPolicyFiles.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path)) return null;

        using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
        });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Any(property => property.Name is not ("$schema" or "schemaVersion" or "steps"))
            || !root.TryGetProperty("$schema", out var schema)
            || schema.ValueKind != JsonValueKind.String
            || !string.Equals(schema.GetString(), QualityAnalysisPolicyFiles.SchemaId, StringComparison.Ordinal)
            || !root.TryGetProperty("schemaVersion", out var version)
            || version.ValueKind != JsonValueKind.Number
            || version.GetInt32() != 1
            || !root.TryGetProperty("steps", out var steps)
            || steps.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"{QualityAnalysisPolicyFiles.RelativePath} must be a v1 Quality Studio pipeline policy object.");
        }

        var known = PipelineCatalogue.QualityAnalysisStepIds.ToHashSet(StringComparer.Ordinal);
        foreach (var step in steps.EnumerateObject())
        {
            if (!known.Contains(step.Name))
                throw new InvalidDataException(
                    $"{QualityAnalysisPolicyFiles.RelativePath} references unknown analysis step '{step.Name}'.");
            if (step.Value.ValueKind != JsonValueKind.Object
                || step.Value.EnumerateObject().Any(property => property.Name != "enabled")
                || !step.Value.TryGetProperty("enabled", out var value)
                || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            {
                throw new InvalidDataException(
                    $"{QualityAnalysisPolicyFiles.RelativePath} override '{step.Name}' must contain one boolean 'enabled'.");
            }
            if (value.GetBoolean()) enabled.Add(step.Name);
            else enabled.Remove(step.Name);
        }
        return QualityAnalysisPolicyFiles.RelativePath;
    }

    private static bool IsAngularPath(string repositoryPath, string path)
    {
        var extension = Path.GetExtension(path);
        if (extension is not (".ts" or ".html" or ".css" or ".scss")) return false;

        var repositoryRoot = Path.GetFullPath(repositoryPath);
        var candidate = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(
            repositoryRoot,
            path.Replace('/', Path.DirectorySeparatorChar))));
        while (candidate is not null && IsWithin(repositoryRoot, candidate))
        {
            if (File.Exists(Path.Combine(candidate, "angular.json"))) return true;
            if (string.Equals(candidate, repositoryRoot, StringComparison.Ordinal)) break;
            candidate = Path.GetDirectoryName(candidate);
        }
        return false;
    }

    private static bool IsWithin(string root, string candidate)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(candidate, root, comparison)
            || candidate.StartsWith(
                root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                comparison);
    }

    private static bool IsDotNetPath(string path)
    {
        var extension = Path.GetExtension(path);
        return extension is ".cs" or ".csproj" or ".fs" or ".fsproj" or ".sln" or ".slnx";
    }

    private static string Normalize(string path) => path.Trim().Replace('\\', '/').TrimStart('/');
}

public sealed record QualityStudioLocation(string Path, int? StartLine, int? StartColumn);

public sealed record QualityStudioFinding(
    string Id,
    string RuleId,
    string Aspect,
    string Severity,
    string Title,
    string Description,
    string Recommendation,
    string Fingerprint,
    string? Evidence,
    IReadOnlyList<QualityStudioLocation> Locations);

public sealed record QualityStudioCoreResult(
    bool Available,
    string? UnavailableReason,
    string Producer,
    string? ProducerVersion,
    IReadOnlyList<QualityStudioFinding> Findings);

public interface IQualityStudioAnalysisCore
{
    Task<QualityStudioCoreResult> RunAsync(
        string repositoryPath,
        string analysisName,
        IReadOnlyDictionary<string, string> configuration,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken);
}

/// <summary>
/// Late-bound consumer of the QS-91 package surface. The package is built and
/// released by Quality Studio, not vendored here. Loading the DLL into this
/// process preserves the in-process boundary while allowing the two repositories
/// to ship independently. No HTTP fallback exists.
/// </summary>
public sealed class QualityStudioAnalysisCoreAdapter : IQualityStudioAnalysisCore
{
    internal const string AssemblyName = "AgentOrchestrator.CodeQuality";
    internal const string RulesAnalysisName = "quality-rules";
    private readonly Func<Assembly?> assemblyResolver;

    public QualityStudioAnalysisCoreAdapter() : this(ResolveAssembly) { }

    internal QualityStudioAnalysisCoreAdapter(Func<Assembly?> assemblyResolver)
        => this.assemblyResolver = assemblyResolver;

    public async Task<QualityStudioCoreResult> RunAsync(
        string repositoryPath,
        string analysisName,
        IReadOnlyDictionary<string, string> configuration,
        IReadOnlyList<string> relativePaths,
        CancellationToken cancellationToken)
    {
        var assembly = assemblyResolver();
        if (assembly is null)
        {
            return Unavailable("Quality Studio analysis-core package is not deployed beside Agent Studio.");
        }

        try
        {
            var coreType = RequireType(assembly, "AgentOrchestrator.CodeQuality.QualityAnalysisCore");
            var sensorType = analysisName == RulesAnalysisName
                ? RequireType(assembly, "AgentOrchestrator.CodeQuality.RulePrecheckSensor")
                : throw new InvalidOperationException($"Analysis '{analysisName}' is not wired by this slice.");
            var sensorInterface = RequireType(assembly, "AgentOrchestrator.CodeQuality.IReviewSensor");
            var sensor = sensorType.GetConstructor(Type.EmptyTypes) is not null
                ? Activator.CreateInstance(sensorType)
                : Activator.CreateInstance(sensorType, [null]);
            sensor = sensor
                ?? throw new InvalidOperationException($"Could not create {sensorType.FullName}.");
            var sensors = Array.CreateInstance(sensorInterface, 1);
            sensors.SetValue(sensor, 0);
            var core = Activator.CreateInstance(coreType, sensors)
                ?? throw new InvalidOperationException($"Could not create {coreType.FullName}.");

            var namedType = RequireType(assembly, "AgentOrchestrator.CodeQuality.NamedQualityAnalysis");
            var scopeType = RequireType(assembly, "AgentOrchestrator.CodeQuality.QualityAnalysisScope");
            var pathScope = Enum.Parse(scopeType, "Path");
            var requestedPaths = relativePaths.Count == 0 ? ["."] : relativePaths;
            var named = Array.CreateInstance(namedType, requestedPaths.Count);
            for (var index = 0; index < requestedPaths.Count; index++)
            {
                named.SetValue(Activator.CreateInstance(
                    namedType, analysisName, configuration, pathScope, requestedPaths[index]), index);
            }

            var requestType = RequireType(assembly, "AgentOrchestrator.CodeQuality.QualityAnalysisRequest");
            var request = Activator.CreateInstance(requestType, repositoryPath, named, false)
                ?? throw new InvalidOperationException($"Could not create {requestType.FullName}.");
            var run = coreType.GetMethod("RunAsync", [requestType, typeof(CancellationToken)])
                ?? throw new MissingMethodException(coreType.FullName, "RunAsync");
            var task = (Task?)run.Invoke(core, [request, cancellationToken])
                ?? throw new InvalidOperationException("Quality Studio returned no analysis task.");
            await task.ConfigureAwait(false);
            var result = task.GetType().GetProperty("Result")?.GetValue(task)
                ?? throw new InvalidOperationException("Quality Studio returned no analysis result.");

            return MapResult(result, assembly.GetName().Version?.ToString());
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Unavailable($"Quality Studio analysis core could not run ({ex.GetBaseException().Message}).");
        }
    }

    private static Assembly? ResolveAssembly()
    {
        var loaded = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(assembly =>
            string.Equals(assembly.GetName().Name, AssemblyName, StringComparison.Ordinal));
        if (loaded is not null) return loaded;
        var path = Path.Combine(AppContext.BaseDirectory, AssemblyName + ".dll");
        return File.Exists(path) ? Assembly.LoadFrom(path) : null;
    }

    private static QualityStudioCoreResult MapResult(object result, string? assemblyVersion)
    {
        var analysisResults = Values(Property(result, "Analyses")).ToArray();
        var available = analysisResults.All(item => (bool?)Property(item, "Available") != false);
        var unavailableReason = analysisResults
            .Select(item => Property(item, "UnavailableReason") as string)
            .FirstOrDefault(reason => !string.IsNullOrWhiteSpace(reason));
        var findings = Values(Property(result, "Findings"))
            .Select(MapFinding)
            .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
            .ToArray();
        return new QualityStudioCoreResult(
            available,
            unavailableReason,
            AssemblyName,
            assemblyVersion,
            findings);
    }

    private static QualityStudioFinding MapFinding(object finding)
    {
        var locations = Values(Property(finding, "Locations")).Select(location =>
        {
            var range = Property(location, "Range");
            var start = range is null ? null : Property(range, "Start");
            return new QualityStudioLocation(
                RequiredString(location, "Path"),
                start is null ? null : Convert.ToInt32(Property(start, "Line")),
                start is null ? null : Convert.ToInt32(Property(start, "Column")));
        }).ToArray();
        return new QualityStudioFinding(
            RequiredString(finding, "Id"),
            RequiredString(finding, "RuleId"),
            RequiredString(finding, "Aspect"),
            Property(finding, "Severity")?.ToString()?.ToLowerInvariant() ?? "info",
            RequiredString(finding, "Title"),
            RequiredString(finding, "Description"),
            RequiredString(finding, "Recommendation"),
            RequiredString(finding, "Fingerprint"),
            Property(finding, "Evidence") as string,
            locations);
    }

    private static QualityStudioCoreResult Unavailable(string reason) =>
        new(false, reason, AssemblyName, null, []);

    private static Type RequireType(Assembly assembly, string name) =>
        assembly.GetType(name, throwOnError: true)!;

    private static object? Property(object value, string name) =>
        value.GetType().GetProperty(name)?.GetValue(value);

    private static string RequiredString(object value, string name) =>
        Property(value, name) as string
        ?? throw new InvalidDataException($"Quality Studio finding is missing '{name}'.");

    private static IEnumerable<object> Values(object? value) =>
        value is IEnumerable items ? items.Cast<object>() : [];
}

public enum QualityAnalysisStepVerdict
{
    NotApplicable,
    Disabled,
    Unavailable,
    Passed,
    Findings,
}

public sealed record QualityAnalysisStepResult(
    string StepId,
    QualityAnalysisStepVerdict Verdict,
    long DurationMs,
    string Reason,
    string? EvidencePath,
    IReadOnlyList<QualityStudioFinding> Findings,
    IReadOnlyList<QualityStudioFinding> BlockingFindings);

public interface IQualityAnalysisStepRunner
{
    Task<QualityAnalysisStepResult> RunAngularRulesAsync(
        string repositoryPath,
        string taskFolderPath,
        IReadOnlyList<string>? changedFiles,
        int? runIndex,
        CancellationToken cancellationToken);
}

public static class QualityAnalysisGatePolicy
{
    /// <summary>
    /// QS-90 policy: security findings are always documented and visible but
    /// do not block this pipeline version. Other implemented axes steer on
    /// medium-or-higher named findings.
    /// </summary>
    public static bool Blocks(string stepId, QualityStudioFinding finding)
    {
        if (string.Equals(stepId, PipelineCatalogue.QualitySecurityStepId, StringComparison.Ordinal)) return false;
        return finding.Severity is "critical" or "high" or "medium";
    }
}

public sealed class QualityAnalysisStepRunner : IQualityAnalysisStepRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly IQualityStudioAnalysisCore core;
    private readonly ILogger<QualityAnalysisStepRunner> logger;

    public QualityAnalysisStepRunner(
        IQualityStudioAnalysisCore core,
        ILogger<QualityAnalysisStepRunner> logger)
    {
        this.core = core;
        this.logger = logger;
    }

    public async Task<QualityAnalysisStepResult> RunAngularRulesAsync(
        string repositoryPath,
        string taskFolderPath,
        IReadOnlyList<string>? changedFiles,
        int? runIndex,
        CancellationToken cancellationToken)
    {
        const string stepId = PipelineCatalogue.QualityAngularRulesStepId;
        QualityAnalysisSelection selection;
        try
        {
            selection = QualityAnalysisPolicy.Resolve(repositoryPath, changedFiles);
        }
        catch (Exception ex)
        {
            return new QualityAnalysisStepResult(
                stepId, QualityAnalysisStepVerdict.Unavailable, 0,
                ex.Message, null, [], []);
        }

        if (!selection.EnabledSteps.Contains(stepId))
        {
            return new QualityAnalysisStepResult(
                stepId,
                selection.AngularPaths.Count > 0
                    ? QualityAnalysisStepVerdict.Disabled
                    : QualityAnalysisStepVerdict.NotApplicable,
                0,
                selection.AngularPaths.Count > 0
                    ? $"repository policy disabled {stepId}"
                    : "card did not touch Angular files",
                null,
                [],
                []);
        }
        if (selection.AngularPaths.Count == 0)
        {
            return new QualityAnalysisStepResult(
                stepId, QualityAnalysisStepVerdict.NotApplicable, 0,
                "no changed Angular files were available for file-scoped analysis", null, [], []);
        }

        var started = DateTime.UtcNow;
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var result = await core.RunAsync(
            repositoryPath,
            QualityStudioAnalysisCoreAdapter.RulesAnalysisName,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["reviewKind"] = "code" },
            selection.AngularPaths,
            cancellationToken);
        timer.Stop();

        var relativeEvidencePath = $"results/quality-analysis/{stepId}.json";
        var evidencePath = Path.Combine(
            taskFolderPath,
            relativeEvidencePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(evidencePath)!);
        var blocking = result.Findings.Where(finding => QualityAnalysisGatePolicy.Blocks(stepId, finding)).ToArray();
        var report = new
        {
            schemaVersion = 1,
            stepId,
            analysis = QualityStudioAnalysisCoreAdapter.RulesAnalysisName,
            policy = new
            {
                source = selection.ConfigurationPath ?? "convention",
                ruleConfiguration = ".quality/rules.json",
                securityFindingsBlock = false,
            },
            startedAt = started,
            completedAt = DateTime.UtcNow,
            result.Available,
            result.UnavailableReason,
            result.Producer,
            result.ProducerVersion,
            changedFiles = selection.AngularPaths,
            findings = result.Findings,
            blockingFindingIds = blocking.Select(finding => finding.Id).ToArray(),
        };
        File.WriteAllText(evidencePath, JsonSerializer.Serialize(report, JsonOptions));

        foreach (var finding in result.Findings)
        {
            ReviewEvidenceLog.Append(taskFolderPath, new ReviewEvidenceEntry
            {
                Id = $"quality-studio:{finding.Fingerprint}",
                Source = ReviewEvidenceSources.CodeReview,
                Severity = EvidenceSeverity(finding.Severity),
                Title = finding.Title,
                RuleId = finding.RuleId,
                Body = $"{finding.Description}\n\nRecommendation: {finding.Recommendation}"
                    + (string.IsNullOrWhiteSpace(finding.Evidence) ? "" : $"\n\nEvidence: {finding.Evidence}"),
                CreatedAt = DateTime.UtcNow,
                RunIndex = runIndex,
                Artifacts = [relativeEvidencePath],
                FileRefs = finding.Locations.Select(LocationReference).ToList(),
            });
        }

        logger.LogInformation(
            "Quality Studio analysis {StepId} completed available={Available} findings={Findings} blocking={Blocking} durationMs={DurationMs}",
            stepId, result.Available, result.Findings.Count, blocking.Length, timer.ElapsedMilliseconds);
        var verdict = !result.Available
            ? QualityAnalysisStepVerdict.Unavailable
            : result.Findings.Count == 0 ? QualityAnalysisStepVerdict.Passed : QualityAnalysisStepVerdict.Findings;
        var reason = !result.Available
            ? result.UnavailableReason ?? "Quality Studio analysis was unavailable"
            : $"{result.Findings.Count} finding(s), {blocking.Length} steering finding(s)";
        return new QualityAnalysisStepResult(
            stepId, verdict, timer.ElapsedMilliseconds, reason,
            relativeEvidencePath, result.Findings, blocking);
    }

    private static string EvidenceSeverity(string severity) => severity switch
    {
        "critical" or "high" => ReviewEvidenceSeverities.High,
        "medium" => ReviewEvidenceSeverities.Warn,
        _ => ReviewEvidenceSeverities.Info,
    };

    private static string LocationReference(QualityStudioLocation location) =>
        location.StartLine is > 0 ? $"{location.Path}:{location.StartLine}" : location.Path;
}
