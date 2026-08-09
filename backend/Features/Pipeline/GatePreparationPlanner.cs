using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>One dependency preparation command that precedes gate verification.</summary>
public sealed record GatePreparationCommand(
    VerifyEcosystem Ecosystem,
    string WorkingSubdir,
    string Command,
    VerifyCommandShell Shell);

/// <summary>
/// Derives dependency preparation for a disposable gate workspace. An explicit
/// build-profile install command is authoritative. Otherwise the same concrete
/// repository and command conventions as <see cref="VerifyCommandPlanner"/>
/// yield <c>dotnet restore</c> and one <c>npm ci</c> per selected package root.
/// </summary>
public static partial class GatePreparationPlanner
{
    private const string DotNetRestore = "dotnet restore";
    private const string NpmCi = "npm ci";

    public static IReadOnlyList<GatePreparationCommand> Plan(
        string repositoryPath,
        BuildProfile? profile,
        IReadOnlyList<VerifyCommand> selectedCommands)
    {
        if (!string.IsNullOrWhiteSpace(profile?.InstallCmd))
        {
            return
            [
                new GatePreparationCommand(
                    VerifyEcosystem.Custom,
                    "",
                    profile.InstallCmd.Trim(),
                    VerifyCommandShell.Bash),
            ];
        }

        var result = new List<GatePreparationCommand>();
        var customCommands = selectedCommands
            .Where(command => command.Ecosystem == VerifyEcosystem.Custom)
            .Select(command => command.Command)
            .ToArray();

        var needsDotNetRestore = selectedCommands.Any(command => command.Ecosystem == VerifyEcosystem.DotNet)
            || customCommands.Any(command => DotNetCommand().IsMatch(command));
        if (needsDotNetRestore && VerifyCommandPlanner.HasDotNetEntryPoint(repositoryPath))
        {
            result.Add(new GatePreparationCommand(
                VerifyEcosystem.DotNet,
                "",
                DotNetRestore,
                customCommands.Length > 0 ? VerifyCommandShell.Bash : VerifyCommandShell.Platform));
        }

        var nodeDirs = new HashSet<string>(
            selectedCommands
                .Where(command => command.Ecosystem == VerifyEcosystem.Node)
                .Select(command => command.WorkingSubdir),
            StringComparer.OrdinalIgnoreCase);

        foreach (var command in customCommands.Where(command => NpmCommand().IsMatch(command)))
        {
            var prefixes = NpmPrefix().Matches(command)
                .Select(match => match.Groups["quoted"].Success
                    ? match.Groups["quoted"].Value
                    : match.Groups["bare"].Value)
                .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
                .Select(prefix => NormalizeRepositorySubdir(repositoryPath, prefix))
                .Where(prefix => prefix is not null)
                .Cast<string>()
                .ToArray();
            if (prefixes.Length > 0)
            {
                foreach (var prefix in prefixes) nodeDirs.Add(prefix);
                continue;
            }

            var discovered = VerifyCommandPlanner.NodePackageDirs(repositoryPath);
            if (discovered.Contains("", StringComparer.OrdinalIgnoreCase))
                nodeDirs.Add("");
            else
                foreach (var subdir in discovered) nodeDirs.Add(subdir);
        }

        foreach (var subdir in nodeDirs.OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            result.Add(new GatePreparationCommand(
                VerifyEcosystem.Node,
                subdir,
                NpmCi,
                customCommands.Length > 0 ? VerifyCommandShell.Bash : VerifyCommandShell.Platform));
        }

        return result;
    }

    private static string? NormalizeRepositorySubdir(string repositoryPath, string prefix)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
            var candidate = Path.GetFullPath(Path.Combine(root, prefix.Replace('/', Path.DirectorySeparatorChar)));
            if (!candidate.Equals(root, StringComparison.OrdinalIgnoreCase)
                && !candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                return null;
            if (!File.Exists(Path.Combine(candidate, "package.json"))) return null;
            var relative = Path.GetRelativePath(root, candidate);
            return relative == "." ? "" : relative;
        }
        catch
        {
            return null;
        }
    }

    [GeneratedRegex(@"\bdotnet(?:\.exe)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DotNetCommand();

    [GeneratedRegex(@"\bnpm(?:\.cmd)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpmCommand();

    [GeneratedRegex("""\bnpm(?:\.cmd)?\s+--prefix(?:=|\s+)(?:["'](?<quoted>[^"']+)["']|(?<bare>[^\s;&|]+))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NpmPrefix();
}
