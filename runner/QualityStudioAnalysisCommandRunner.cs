using System.Collections;
using System.Reflection;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>
/// Executes the Quality Studio rule sensor from the
/// AgentOrchestrator.CodeQuality package in the Review Host process. Reflection
/// keeps the wire host buildable until QS-91 publishes the package, while the
/// invoked type and constructor are the concrete QS-90 package contract. There
/// is no HTTP fallback.
/// </summary>
internal sealed class QualityStudioAnalysisCommandRunner
{
    internal const string PackageAssemblyName = "AgentOrchestrator.CodeQuality";
    internal const string RuleSensorTypeName =
        "AgentOrchestrator.CodeQuality.RulePrecheckSensor";
    internal const string SensorRequestTypeName =
        "AgentOrchestrator.CodeQuality.SensorScanRequest";
    internal const string SensorScopeTypeName =
        "AgentOrchestrator.CodeQuality.SensorScope";

    private readonly Func<Assembly> _loadAssembly;

    public QualityStudioAnalysisCommandRunner()
        : this(() => Assembly.Load(PackageAssemblyName))
    {
    }

    internal QualityStudioAnalysisCommandRunner(Func<Assembly> loadAssembly)
        => _loadAssembly = loadAssembly;

    public async Task<CommandExecution> RunAsync(
        ReviewCommandDto command,
        string repositoryPath,
        CancellationToken ct)
    {
        var started = DateTime.UtcNow;
        try
        {
            if (!string.Equals(command.FileName, "quality-rules", StringComparison.Ordinal))
                throw new InvalidOperationException(
                    $"Unsupported Quality Studio analysis '{command.FileName}'.");

            var assembly = _loadAssembly();
            var sensorType = RequiredType(assembly, RuleSensorTypeName);
            var requestType = RequiredType(assembly, SensorRequestTypeName);
            var scopeType = RequiredType(assembly, SensorScopeTypeName);
            var sensor = CreateSensor(sensorType);
            var runMethod = sensorType.GetMethod(
                "RunAsync",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: [requestType, typeof(CancellationToken)],
                modifiers: null)
                ?? throw new MissingMethodException(sensorType.FullName, "RunAsync");
            var pathScope = Enum.Parse(scopeType, "Path", ignoreCase: false);
            var configuration = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["reviewKind"] = "code",
            };
            var findings = new List<QualityStudioFindingEvidence>();
            var available = true;
            string? unavailableReason = null;

            foreach (var path in command.Arguments.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var request = Activator.CreateInstance(
                    requestType,
                    repositoryPath,
                    pathScope,
                    path,
                    configuration,
                    false)
                    ?? throw new MissingMethodException(requestType.FullName, ".ctor");
                var pending = runMethod.Invoke(sensor, [request, ct]) as Task
                    ?? throw new InvalidOperationException(
                        $"{sensorType.FullName}.RunAsync did not return Task.");
                await pending.WaitAsync(ct).ConfigureAwait(false);
                var result = pending.GetType().GetProperty("Result")?.GetValue(pending)
                    ?? throw new InvalidOperationException(
                        $"{sensorType.FullName}.RunAsync returned no result.");
                available &= Read<bool>(result, "Available");
                unavailableReason ??= Read<string?>(result, "UnavailableReason");
                findings.AddRange(ReadFindings(result));
            }

            var report = new QualityStudioAnalysisEvidence(
                SchemaVersion: 1,
                StepId: command.StepId,
                Analysis: command.FileName,
                Axis: command.Aspect,
                Provider: PackageAssemblyName,
                ProviderVersion: assembly.GetName().Version?.ToString() ?? "unknown",
                ConfigurationPath: ".quality/rules.json",
                Available: available,
                UnavailableReason: unavailableReason,
                SecurityFindingsBlockPipeline: false,
                Findings: findings
                    .DistinctBy(finding => finding.Fingerprint, StringComparer.Ordinal)
                    .OrderBy(finding => finding.Path, StringComparer.Ordinal)
                    .ThenBy(finding => finding.Line)
                    .ThenBy(finding => finding.RuleId, StringComparer.Ordinal)
                    .ToArray());
            var json = JsonSerializer.Serialize(report, QualityStudioAnalysisEvidence.JsonOptions);
            return new CommandExecution(
                new ProcessResult(report.Findings.Count == 0 && report.Available ? 0 : 1, json, string.Empty),
                started,
                DateTime.UtcNow,
                Signal: null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var cause = exception is TargetInvocationException { InnerException: not null }
                ? exception.InnerException
                : exception;
            return new CommandExecution(
                new ProcessResult(
                    127,
                    string.Empty,
                    $"Quality Studio in-process analysis is unavailable: {cause!.Message}"),
                started,
                DateTime.UtcNow,
                Signal: null);
        }
    }

    private static IEnumerable<QualityStudioFindingEvidence> ReadFindings(object result)
    {
        var values = Read<object>(result, "Findings") as IEnumerable
            ?? throw new InvalidDataException("Quality Studio result.Findings is not enumerable.");
        foreach (var value in values)
        {
            if (value is null) continue;
            var location = (Read<object>(value, "Locations") as IEnumerable)?
                .Cast<object>()
                .FirstOrDefault();
            var range = location is null ? null : Read<object?>(location, "Range");
            var start = range is null ? null : Read<object?>(range, "Start");
            yield return new QualityStudioFindingEvidence(
                Id: Read<string>(value, "Id"),
                RuleId: Read<string>(value, "RuleId"),
                Severity: Read<object>(value, "Severity").ToString()?.ToLowerInvariant() ?? "unknown",
                Title: Read<string>(value, "Title"),
                Description: Read<string>(value, "Description"),
                Recommendation: Read<string>(value, "Recommendation"),
                Fingerprint: Read<string>(value, "Fingerprint"),
                Path: location is null ? string.Empty : Read<string>(location, "Path"),
                Line: start is null ? null : Read<int>(start, "Line"),
                Column: start is null ? null : Read<int>(start, "Column"));
        }
    }

    private static Type RequiredType(Assembly assembly, string name)
        => assembly.GetType(name, throwOnError: false, ignoreCase: false)
           ?? throw new TypeLoadException(
               $"Quality Studio package does not expose required type '{name}'.");

    private static object CreateSensor(Type sensorType)
    {
        var constructor = sensorType.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
            .OrderBy(candidate => candidate.GetParameters().Length)
            .FirstOrDefault(candidate => candidate.GetParameters().All(parameter => parameter.HasDefaultValue))
            ?? throw new MissingMethodException(
                sensorType.FullName,
                ".ctor with only optional parameters");
        var arguments = constructor.GetParameters()
            .Select(parameter => parameter.DefaultValue)
            .ToArray();
        return constructor.Invoke(arguments);
    }

    private static T Read<T>(object instance, string property)
    {
        var value = instance.GetType().GetProperty(property)?.GetValue(instance);
        if (value is null && default(T) is not null)
            throw new InvalidDataException(
                $"Quality Studio value '{instance.GetType().FullName}.{property}' is missing.");
        return (T)value!;
    }
}

internal sealed record QualityStudioAnalysisEvidence(
    int SchemaVersion,
    string StepId,
    string Analysis,
    string Axis,
    string Provider,
    string ProviderVersion,
    string ConfigurationPath,
    bool Available,
    string? UnavailableReason,
    bool SecurityFindingsBlockPipeline,
    IReadOnlyList<QualityStudioFindingEvidence> Findings)
{
    internal static JsonSerializerOptions JsonOptions { get; } = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static bool TryParse(string json, out QualityStudioAnalysisEvidence? report)
    {
        try
        {
            report = JsonSerializer.Deserialize<QualityStudioAnalysisEvidence>(json, JsonOptions);
            return report is { SchemaVersion: 1 };
        }
        catch (JsonException)
        {
            report = null;
            return false;
        }
    }

    public string VerdictSummary()
    {
        if (!Available)
            return $"Quality Studio analysis '{Axis}' was unavailable: {UnavailableReason}";
        if (Findings.Count == 0)
            return $"Quality Studio analysis '{Axis}' passed with no findings.";
        var refs = Findings.Take(8).Select(finding =>
            $"{finding.RuleId} {finding.Path}" +
            (finding.Line is null ? string.Empty : $":{finding.Line}"));
        return $"Quality Studio analysis '{Axis}' found {Findings.Count} violation(s): " +
               string.Join("; ", refs) + ".";
    }
}

internal sealed record QualityStudioFindingEvidence(
    string Id,
    string RuleId,
    string Severity,
    string Title,
    string Description,
    string Recommendation,
    string Fingerprint,
    string Path,
    int? Line,
    int? Column);
