using System.Text.Json;

using AgentStudio.Diagnostics;

namespace AgentStudio.HostHealth;

/// <summary>
/// The npm-installed coding-agent CLIs this feature knows how to diagnose and
/// reinstall. The command name is what lands in the npm global bin directory;
/// the package id is what a global reinstall names.
/// </summary>
public sealed record LocalCliPackage(string CliType, string Command, string PackageId)
{
    /// <summary>
    /// Claude and Codex only. Gemini/Antigravity is not distributed as a
    /// single global npm package on the control plane, so it has no reinstall
    /// remedy this feature could offer.
    /// </summary>
    public static readonly IReadOnlyList<LocalCliPackage> Known =
    [
        new("claude", "claude", "@anthropic-ai/claude-code"),
        new("codex", "codex", "@openai/codex"),
    ];

    public static LocalCliPackage? Find(string? cliType)
        => Known.FirstOrDefault(p => string.Equals(p.CliType, cliType, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Reads the host filesystem into <see cref="LocalCliInstallFacts"/>. This is
/// the only place in the feature that touches disk during diagnosis; the
/// classification that follows is pure.
/// </summary>
public sealed class LocalCliInstallInspector
{
    private readonly ILogger<LocalCliInstallInspector> _logger;
    private readonly NpmGlobalLayout _layout;
    private readonly bool _isWindows;

    public LocalCliInstallInspector(IConfiguration configuration, ILogger<LocalCliInstallInspector> logger)
        : this(logger, ResolveLayout(configuration), OperatingSystem.IsWindows())
    {
    }

    /// <summary>Test seam: inject a layout rooted at a temporary directory.</summary>
    internal LocalCliInstallInspector(ILogger<LocalCliInstallInspector> logger, NpmGlobalLayout layout, bool isWindows)
    {
        _logger = logger;
        _layout = layout;
        _isWindows = isWindows;
    }

    public NpmGlobalLayout Layout => _layout;

    /// <summary>
    /// Combine a <c>--version</c> verdict (owned by the CLI layer) with what
    /// the npm global install looks like right now.
    /// </summary>
    public LocalCliInstallFacts Inspect(LocalCliPackage package, bool versionProbeOk, string? probedVersion)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (!_layout.Resolved)
        {
            return new LocalCliInstallFacts
            {
                CliType = package.CliType,
                PackageId = package.PackageId,
                VersionProbeOk = versionProbeOk,
                ProbedVersion = probedVersion,
                NpmGlobalBinResolved = false,
            };
        }

        var packageDirectory = Path.Combine(
            _layout.NodeModulesDirectory!,
            package.PackageId.Replace('/', Path.DirectorySeparatorChar));

        return new LocalCliInstallFacts
        {
            CliType = package.CliType,
            PackageId = package.PackageId,
            VersionProbeOk = versionProbeOk,
            ProbedVersion = probedVersion,
            NpmGlobalBinResolved = true,
            ShimPresent = AnyShimPresent(package.Command),
            OrphanShimsPresent = AnyOrphanShimPresent(package.Command),
            PackagePresent = SafeDirectoryExists(packageDirectory),
            PackageVersion = ReadPackageVersion(packageDirectory),
        };
    }

    /// <summary>
    /// npm debug logs written shortly before <paramref name="observedAtUtc"/>.
    /// Empty when the logs directory is unknown or unreadable; the absence of
    /// evidence is journalled as such rather than raised as an error.
    /// </summary>
    public IReadOnlyList<NpmLogFile> RecentNpmActivity(DateTime observedAtUtc, TimeSpan lookBack)
    {
        if (string.IsNullOrEmpty(_layout.LogsDirectory)) return Array.Empty<NpmLogFile>();

        try
        {
            if (!Directory.Exists(_layout.LogsDirectory)) return Array.Empty<NpmLogFile>();
            var files = new DirectoryInfo(_layout.LogsDirectory)
                .EnumerateFiles("*.log")
                .Select(file => new NpmLogFile(file.Name, file.LastWriteTimeUtc, file.Length));
            return NpmActivityWindow.Select(files, observedAtUtc, lookBack);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not enumerate npm logs in {Directory}", _layout.LogsDirectory);
            return Array.Empty<NpmLogFile>();
        }
    }

    private bool AnyShimPresent(string command)
        => LocalCliShimNames.LaunchableShims(command, _isWindows)
            .Any(name => SafeFileExists(Path.Combine(_layout.BinDirectory!, name)));

    private bool AnyOrphanShimPresent(string command)
    {
        try
        {
            if (!Directory.Exists(_layout.BinDirectory)) return false;
            return Directory
                .EnumerateFiles(_layout.BinDirectory!, "." + command + "*")
                .Any(path => LocalCliShimNames.IsOrphanShim(Path.GetFileName(path), command));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not scan {Directory} for orphaned {Command} shims", _layout.BinDirectory, command);
            return false;
        }
    }

    private string? ReadPackageVersion(string packageDirectory)
    {
        var manifest = Path.Combine(packageDirectory, "package.json");
        try
        {
            if (!File.Exists(manifest)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(manifest));
            return document.RootElement.TryGetProperty("version", out var version)
                ? version.GetString()
                : null;
        }
        catch (Exception ex)
        {
            // A torn package.json is itself a symptom of an interrupted
            // install, so a missing version must not abort the diagnosis.
            _logger.LogDebug(ex, "Could not read the installed version from {Manifest}", manifest);
            return null;
        }
    }

    private static bool SafeFileExists(string path)
    {
        try { return File.Exists(path); }
        catch (Exception ex) { SilentCatch.Note(ex, $"LocalCliInstallInspector: File.Exists('{path}')"); return false; }
    }

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (Exception ex) { SilentCatch.Note(ex, $"LocalCliInstallInspector: Directory.Exists('{path}')"); return false; }
    }

    private static NpmGlobalLayout ResolveLayout(IConfiguration configuration)
        => NpmGlobalLayoutResolver.Resolve(
            new NpmEnvironment
            {
                ConfiguredBin = configuration["HostHealth:NpmGlobalBin"],
                IsWindows = OperatingSystem.IsWindows(),
                AppData = Environment.GetEnvironmentVariable("APPDATA"),
                LocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
                NpmConfigPrefix = Environment.GetEnvironmentVariable("NPM_CONFIG_PREFIX"),
                Home = Environment.GetEnvironmentVariable("HOME")
                       ?? Environment.GetEnvironmentVariable("USERPROFILE"),
            },
            static path => { try { return Directory.Exists(path); } catch (Exception ex) { SilentCatch.Note(ex, "NpmGlobalLayoutResolver: Directory.Exists"); return false; } });
}
