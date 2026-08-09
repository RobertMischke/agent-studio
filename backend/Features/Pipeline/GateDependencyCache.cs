using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Pipeline;

/// <summary>
/// Moves dependency and Angular caches between disposable exact-subject
/// worktrees and one stable cache owned by the source repository. Moving on the
/// same volume is constant-time and leaves <c>git worktree remove</c> with only
/// tracked build inputs to delete.
/// </summary>
internal sealed class GateDependencyCacheSession
{
    private static readonly string[] PreservedNames =
    [
        AgentStudio.Runner.DepsState.DepsDirName,
        ".angular",
    ];

    private readonly string _workspace;
    private readonly string _cacheRoot;
    private readonly IReadOnlyList<string> _workingSubdirs;
    private readonly ILogger _logger;

    private GateDependencyCacheSession(
        string workspace,
        string cacheRoot,
        IReadOnlyList<string> workingSubdirs,
        ILogger logger)
    {
        _workspace = workspace;
        _cacheRoot = cacheRoot;
        _workingSubdirs = workingSubdirs;
        _logger = logger;
    }

    public static GateDependencyCacheSession Create(
        string reviewWorkspaceRoot,
        string repositoryPath,
        string workspace,
        IReadOnlyList<GatePreparationCommand> preparation,
        IReadOnlyList<VerifyCommand> commands,
        ILogger logger)
    {
        var subdirs = preparation
            .SelectMany(command => command.DependencyScopes)
            .Select(scope => scope.WorkingSubdir)
            .Concat(commands
                .Where(command => command.Ecosystem == VerifyEcosystem.Node)
                .Select(command => command.WorkingSubdir))
            .Select(NormalizeSubdir)
            .Where(subdir => subdir is not null)
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(subdir => subdir, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var cacheRoot = Path.Combine(
            reviewWorkspaceRoot,
            BuildTestGateRunner.DependencyCacheDirectoryName,
            RepositoryKey(repositoryPath));
        return new GateDependencyCacheSession(workspace, cacheRoot, subdirs, logger);
    }

    public IReadOnlyList<string> Restore()
        => Transfer(restore: true);

    public IReadOnlyList<string> Save()
        => Transfer(restore: false);

    internal static string CachePath(string reviewWorkspaceRoot, string repositoryPath)
        => Path.Combine(
            reviewWorkspaceRoot,
            BuildTestGateRunner.DependencyCacheDirectoryName,
            RepositoryKey(repositoryPath));

    private IReadOnlyList<string> Transfer(bool restore)
    {
        var operation = restore ? "restore" : "save";
        var messages = new List<string>();
        var stopwatch = Stopwatch.StartNew();
        foreach (var subdir in _workingSubdirs)
        {
            var workspaceRoot = ResolveWithin(_workspace, subdir);
            var cacheRoot = ResolveWithin(Path.Combine(_cacheRoot, "content"), subdir);
            if (workspaceRoot is null || cacheRoot is null) continue;

            foreach (var name in PreservedNames)
            {
                var workspacePath = Path.Combine(workspaceRoot, name);
                var cachePath = Path.Combine(cacheRoot, name);
                MoveDirectory(
                    restore ? cachePath : workspacePath,
                    restore ? workspacePath : cachePath,
                    operation,
                    subdir,
                    name,
                    messages);
            }

            var workspaceMarker = Path.Combine(
                workspaceRoot,
                AgentStudio.Runner.DepsState.MarkerFileName);
            var cacheMarker = Path.Combine(
                cacheRoot,
                AgentStudio.Runner.DepsState.MarkerFileName);
            MoveFile(
                restore ? cacheMarker : workspaceMarker,
                restore ? workspaceMarker : cacheMarker,
                operation,
                subdir,
                messages);
        }

        stopwatch.Stop();
        messages.Add(
            $"dependency-cache {operation} repository={Path.GetFileName(_cacheRoot)} " +
            $"scopes={_workingSubdirs.Count} durationMs={stopwatch.ElapsedMilliseconds}");
        return messages;
    }

    private void MoveDirectory(
        string source,
        string destination,
        string operation,
        string subdir,
        string name,
        ICollection<string> messages)
    {
        if (!Directory.Exists(source)) return;
        try
        {
            if (Directory.Exists(destination))
            {
                if (operation == "restore")
                {
                    messages.Add($"dependency-cache restore skipped scope={Display(subdir)} item={name} reason=destination-exists");
                    return;
                }
                Directory.Delete(destination, recursive: true);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            Directory.Move(source, destination);
            messages.Add($"dependency-cache {operation} scope={Display(subdir)} item={name} state=moved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "build_test_gate_dependency_cache_{Operation}_failed workspace={Workspace} scope={Scope} item={Item}",
                operation,
                _workspace,
                Display(subdir),
                name);
            messages.Add($"dependency-cache {operation} scope={Display(subdir)} item={name} state=failed reason={ex.GetType().Name}");
        }
    }

    private void MoveFile(
        string source,
        string destination,
        string operation,
        string subdir,
        ICollection<string> messages)
    {
        if (!File.Exists(source)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Move(source, destination, overwrite: operation == "save");
            messages.Add(
                $"dependency-cache {operation} scope={Display(subdir)} " +
                $"item={AgentStudio.Runner.DepsState.MarkerFileName} state=moved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(
                ex,
                "build_test_gate_dependency_cache_{Operation}_failed workspace={Workspace} scope={Scope} item={Item}",
                operation,
                _workspace,
                Display(subdir),
                AgentStudio.Runner.DepsState.MarkerFileName);
            messages.Add(
                $"dependency-cache {operation} scope={Display(subdir)} " +
                $"item={AgentStudio.Runner.DepsState.MarkerFileName} state=failed reason={ex.GetType().Name}");
        }
    }

    private static string RepositoryKey(string repositoryPath)
    {
        var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryPath));
        if (OperatingSystem.IsWindows()) canonical = canonical.ToUpperInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..24];
    }

    private static string? NormalizeSubdir(string? subdir)
    {
        if (string.IsNullOrWhiteSpace(subdir) || subdir == ".") return "";
        var normalized = subdir.Replace('\\', '/').Trim('/');
        if (Path.IsPathRooted(normalized)
            || normalized.Split('/').Any(part => part is "" or "." or ".."))
            return null;
        return normalized;
    }

    private static string? ResolveWithin(string root, string subdir)
    {
        var canonicalRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var candidate = string.IsNullOrEmpty(subdir)
            ? canonicalRoot
            : Path.GetFullPath(Path.Combine(
                canonicalRoot,
                subdir.Replace('/', Path.DirectorySeparatorChar)));
        return candidate.Equals(canonicalRoot, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(canonicalRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    private static string Display(string subdir)
        => string.IsNullOrEmpty(subdir) ? "." : subdir;
}
