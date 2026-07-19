using System.Text.RegularExpressions;

namespace AgentStudio.Projects;

public sealed record CompileDeploymentPromptRequest(string Prompt);

public sealed record CompiledDeploymentPrompt(
    string Title,
    string Summary,
    string? Command,
    IReadOnlyList<ProjectDeploymentParameter> Parameters,
    IReadOnlyList<string> Warnings,
    bool Runnable);

/// <summary>
/// Deterministic first PDU compiler. It accepts only repository-owned scripts
/// and typed {{slot}} placeholders. Anything that cannot be bounded is returned
/// for review with Runnable=false and can never reach the task launcher.
/// </summary>
public sealed partial class ProjectDeploymentCompiler
{
    private static readonly HashSet<string> AllowedTypes =
        ["string", "boolean", "branch", "enum", "secret-ref"];

    public CompiledDeploymentPrompt Compile(string prompt)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(prompt))
            return new("Deployment", "No deployment description supplied.", null, [], ["Describe the deployment first."], false);

        var command = CommandLine().Match(prompt) is { Success: true } commandMatch
            ? commandMatch.Groups["command"].Value.Trim().Trim('`')
            : null;
        if (string.IsNullOrWhiteSpace(command))
            warnings.Add("Add a 'Command:' line that invokes a repository-owned script.");
        else if (!SafeCommand().IsMatch(command) || DangerousShell().IsMatch(RemoveSlots(command)))
            warnings.Add("The command must be a repository-owned scripts/*.sh path with typed slots and no shell chaining or redirection.");

        var declared = ParameterLine().Matches(prompt)
            .Select(match => new
            {
                Name = match.Groups["name"].Value,
                Type = match.Groups["type"].Value.ToLowerInvariant(),
                Required = !string.Equals(match.Groups["optional"].Value, "optional", StringComparison.OrdinalIgnoreCase),
            })
            .Where(parameter => AllowedTypes.Contains(parameter.Type))
            .ToDictionary(parameter => parameter.Name, StringComparer.OrdinalIgnoreCase);
        var parameters = new List<ProjectDeploymentParameter>();
        if (command is not null)
        {
            foreach (Match slot in Slot().Matches(command))
            {
                var name = slot.Groups["name"].Value;
                if (parameters.Any(parameter => string.Equals(parameter.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                if (!declared.TryGetValue(name, out var definition))
                {
                    warnings.Add($"Declare slot '{{{{{name}}}}}' with 'Parameter: {name} <type>'.");
                    continue;
                }
                parameters.Add(new(definition.Name, definition.Type, definition.Required, null, []));
            }
        }

        parameters.Add(new("confirm", "boolean", true, System.Text.Json.JsonSerializer.SerializeToElement(false), []));
        var runnable = command is not null && warnings.Count == 0;
        var firstLine = prompt.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
        return new(
            string.IsNullOrWhiteSpace(firstLine) ? "Prompt-defined deployment" : firstLine.TrimStart('#', ' '),
            "Human-reviewed prompt-defined deployment compiled to typed slots.",
            runnable ? command : null,
            parameters,
            warnings,
            runnable);
    }

    private static string RemoveSlots(string command) => Slot().Replace(command, "slot");

    [GeneratedRegex(@"(?im)^\s*command\s*:\s*(?<command>.+)$")]
    private static partial Regex CommandLine();

    [GeneratedRegex(@"(?im)^\s*parameter\s*:\s*(?<name>[A-Za-z][A-Za-z0-9_-]*)\s+(?<type>string|boolean|branch|enum|secret-ref)(?:\s+(?<optional>optional))?\s*$")]
    private static partial Regex ParameterLine();

    [GeneratedRegex(@"\{\{(?<name>[A-Za-z][A-Za-z0-9_-]*)\}\}")]
    private static partial Regex Slot();

    [GeneratedRegex(@"^(?:bash\s+)?scripts/[A-Za-z0-9_./-]+\.sh(?:\s+(?:[A-Za-z0-9_./:=@+-]+|\{\{[A-Za-z][A-Za-z0-9_-]*\}\}))*$")]
    private static partial Regex SafeCommand();

    [GeneratedRegex(@"(?:;|&&|\|\||\||>|<|\$\(|`)")]
    private static partial Regex DangerousShell();
}
