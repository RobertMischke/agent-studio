using System.Text.Json;

namespace AgentStudio.Pipeline;

/// <summary>Which stack a derived verify command belongs to.</summary>
public enum VerifyEcosystem
{
    /// <summary>A bare, auto-discovering <c>dotnet</c> command run at the repo root.</summary>
    DotNet,

    /// <summary>An <c>npm</c> script command run in a package.json directory.</summary>
    Node,

    /// <summary>A verbatim command taken from an explicit build profile.</summary>
    Custom,
}

/// <summary>What a derived verify command checks.</summary>
public enum VerifyCommandKind
{
    Build,
    Test,
    Lint,
}

/// <summary>Shell contract for a verification command.</summary>
public enum VerifyCommandShell
{
    /// <summary>Use the host shell for convention-derived tool commands.</summary>
    Platform,

    /// <summary>
    /// Use <c>bash -lc</c>. Explicit build-profile commands use the same shell
    /// as the build-profile validation dry-run on every host.
    /// </summary>
    Bash,
}

/// <summary>
/// One derived verify command: the shell command plus the repo-relative working
/// directory it runs in. Deliberately a plain shell string so the same shape
/// carries both an auto-discovered <c>dotnet build</c> and an explicit
/// build-profile command verbatim.
/// </summary>
public sealed record VerifyCommand(
    VerifyEcosystem Ecosystem,
    VerifyCommandKind Kind,
    string WorkingSubdir,
    string Command)
{
    /// <summary>Shell used to execute the command.</summary>
    public VerifyCommandShell Shell { get; init; } = VerifyCommandShell.Platform;

    /// <summary>Coverage scope stamped by the staged test selector.</summary>
    public string TestScope { get; init; } = "not-test";

    /// <summary>
    /// False only for continuous baseline tests during a work-package run. A
    /// red baseline is recorded as a separate finding and does not charge the
    /// current card for unrelated project debt.
    /// </summary>
    public bool BlocksWorkPackage { get; init; } = true;

    /// <summary>Human-readable selection provenance persisted with the gate.</summary>
    public string? SelectionReason { get; init; }
}

/// <summary>The derived verify plan plus where it came from (for logs / the verdict).</summary>
public sealed record VerifyPlan(IReadOnlyList<VerifyCommand> Commands, string Source)
{
    /// <summary>An explicit build profile supplied the build/test commands (the override).</summary>
    public const string SourceBuildProfile = "build-profile";

    /// <summary>Commands were derived from the repo layout (.sln/.csproj, package.json scripts).</summary>
    public const string SourceAutoDiscovery = "auto-discovery";

    /// <summary>Nothing was derivable; the gate runs without a build check (honest fallback).</summary>
    public const string SourceNone = "none";

    public bool IsEmpty => Commands.Count == 0;
}

/// <summary>
/// Pure planner (AGT-2065) that derives the deterministic verify set for a repo
/// instead of hardcoding a Studio-specific command. Convention over settings
/// (the house rule):
/// <list type="number">
///   <item>An explicit <see cref="BuildProfile"/> with build/test commands is the
///     override and wins outright.</item>
///   <item>Otherwise the commands are auto-discovered from the repo layout: a
///     <c>.sln</c>/<c>.slnx</c>/<c>.csproj</c> at the repo root yields bare
///     <c>dotnet build</c> + <c>dotnet test</c> (auto-discovery, no hardcoded
///     project path); a <c>package.json</c> (root or one level deep) yields
///     <c>npm run build</c> / <c>npm test</c> / <c>npm run lint</c> for whichever
///     of those scripts the manifest actually declares. A repo with both gets
///     both.</item>
///   <item>When nothing is derivable the plan is empty (<see cref="VerifyPlan.SourceNone"/>)
///     so the caller can run the gate without a build check and say so, rather
///     than fail against a path that does not exist.</item>
/// </list>
/// The Studio-specific hardcode (<c>backend/OrchestratorApi.csproj</c>) broke on
/// every new project whose layout differed (TE-2, 2026-07-10); deriving per repo
/// fixes that at the root.
/// </summary>
public static class VerifyCommandPlanner
{
    /// <summary>
    /// The npm default "no test specified" placeholder. Treated as "no test
    /// script" so a scaffolded manifest does not turn the gate red spuriously.
    /// </summary>
    private const string NpmDefaultTestMarker = "no test specified";

    /// <summary>
    /// Directory names skipped during the one-level package.json scan so the
    /// discovery stays bounded and convention-driven, never a full tree walk.
    /// </summary>
    private static readonly HashSet<string> IgnoredScanDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", "dist", "out", "build",
        "target", "packages", "vendor", "coverage", "test-results",
    };

    /// <summary>
    /// Derives the ordered verify plan for <paramref name="repositoryPath"/>. An
    /// explicit <paramref name="profile"/> with build/test commands overrides the
    /// auto-discovery entirely.
    /// </summary>
    public static VerifyPlan Plan(string repositoryPath, BuildProfile? profile)
    {
        var fromProfile = FromProfile(profile);
        if (fromProfile.Count > 0)
            return new VerifyPlan(fromProfile, VerifyPlan.SourceBuildProfile);

        var derived = AutoDiscover(repositoryPath);
        if (derived.Count > 0)
            return new VerifyPlan(derived, VerifyPlan.SourceAutoDiscovery);

        return new VerifyPlan(Array.Empty<VerifyCommand>(), VerifyPlan.SourceNone);
    }

    /// <summary>
    /// True when <paramref name="profile"/> declares at least one real build
    /// command, i.e. the profile itself is the source of a build check and no
    /// auto-discovery guesswork is involved. Convention instead of a settings
    /// switch: the pre-develop build gate runs exactly for those projects.
    /// </summary>
    public static bool HasProfileBuildCommands(BuildProfile? profile)
        => (profile?.BuildCmds ?? Array.Empty<string>())
            .Any(command => !string.IsNullOrWhiteSpace(command));

    /// <summary>
    /// The explicit override: the profile's build commands then its test commands,
    /// verbatim, in declared order. Empty when the profile declares neither (a
    /// profile with only install/lockfile metadata falls through to discovery).
    /// </summary>
    private static IReadOnlyList<VerifyCommand> FromProfile(BuildProfile? profile)
    {
        var cmds = new List<VerifyCommand>();
        if (profile is null) return cmds;

        foreach (var build in profile.BuildCmds ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(build))
                cmds.Add(new VerifyCommand(VerifyEcosystem.Custom, VerifyCommandKind.Build, "", build.Trim())
                {
                    Shell = VerifyCommandShell.Bash,
                });

        foreach (var test in profile.TestCmds ?? Array.Empty<string>())
            if (!string.IsNullOrWhiteSpace(test))
                cmds.Add(new VerifyCommand(VerifyEcosystem.Custom, VerifyCommandKind.Test, "", test.Trim())
                {
                    Shell = VerifyCommandShell.Bash,
                });

        return cmds;
    }

    private static IReadOnlyList<VerifyCommand> AutoDiscover(string repositoryPath)
    {
        var cmds = new List<VerifyCommand>();
        if (string.IsNullOrWhiteSpace(repositoryPath) || !Directory.Exists(repositoryPath))
            return cmds;

        // .NET: a solution or project file at the repo root means a bare,
        // auto-discovering `dotnet build` / `dotnet test` resolves the whole
        // stack. Root-only on purpose - if the only project lives a level down,
        // a root `dotnet build` cannot pick it and we must not pretend it can.
        if (HasDotNetEntryPoint(repositoryPath))
        {
            cmds.Add(new VerifyCommand(VerifyEcosystem.DotNet, VerifyCommandKind.Build, "", "dotnet build"));
            cmds.Add(new VerifyCommand(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test"));
        }

        // Node: derive whichever of build / test / lint scripts the manifest
        // actually declares - never invent a script the repo does not define.
        foreach (var subdir in NodePackageDirs(repositoryPath))
        {
            var manifest = Path.Combine(repositoryPath, subdir, "package.json");
            var scripts = ReadNpmScripts(manifest);

            if (HasScript(scripts, "build"))
                cmds.Add(new VerifyCommand(VerifyEcosystem.Node, VerifyCommandKind.Build, subdir, "npm run build"));
            if (HasScript(scripts, "test"))
                cmds.Add(new VerifyCommand(VerifyEcosystem.Node, VerifyCommandKind.Test, subdir, "npm test"));
            if (HasScript(scripts, "lint"))
                cmds.Add(new VerifyCommand(VerifyEcosystem.Node, VerifyCommandKind.Lint, subdir, "npm run lint"));
        }

        return cmds;
    }

    /// <summary>
    /// True when the repo root carries a solution or project file, so bare
    /// <c>dotnet build</c> / <c>dotnet test</c> at the root resolve it. Enumerates
    /// files once and matches extensions explicitly to dodge the Windows
    /// <c>*.sln</c>-also-matches-<c>*.slnx</c> search-pattern quirk.
    /// </summary>
    internal static bool HasDotNetEntryPoint(string repoRoot)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(repoRoot))
            {
                var ext = Path.GetExtension(file);
                if (ext.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".slnx", StringComparison.OrdinalIgnoreCase)
                    || ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "VerifyCommandPlanner: dotnet entry-point probe");
        }
        return false;
    }

    /// <summary>
    /// Repo-relative directories holding a <c>package.json</c>: the repo root plus
    /// each immediate child directory (frontend/, web/, ...). Bounded to one level
    /// and skips dependency / build-output folders.
    /// </summary>
    internal static IReadOnlyList<string> NodePackageDirs(string repoRoot)
    {
        var dirs = new List<string>();
        if (File.Exists(Path.Combine(repoRoot, "package.json")))
            dirs.Add("");

        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(repoRoot); }
        catch (Exception ex)
        {
            SilentCatch.Note(ex, "VerifyCommandPlanner: package.json child scan");
            return dirs;
        }

        foreach (var child in children)
        {
            var name = Path.GetFileName(child);
            if (string.IsNullOrEmpty(name) || name.StartsWith('.') || IgnoredScanDirs.Contains(name))
                continue;
            if (File.Exists(Path.Combine(child, "package.json")))
                dirs.Add(name);
        }
        return dirs;
    }

    /// <summary>Reads the <c>scripts</c> map from a package.json (name -> command).</summary>
    private static IReadOnlyDictionary<string, string> ReadNpmScripts(string packageJsonPath)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (!File.Exists(packageJsonPath)) return map;
            using var doc = JsonDocument.Parse(File.ReadAllText(packageJsonPath));
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("scripts", out var scripts)
                && scripts.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in scripts.EnumerateObject())
                    map[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : "";
            }
        }
        catch (Exception ex)
        {
            // An unreadable / invalid manifest derives nothing from node - honest,
            // not a crash. The dotnet branch (if any) still stands on its own.
            SilentCatch.Note(ex, "VerifyCommandPlanner: package.json parse");
        }
        return map;
    }

    private static bool HasScript(IReadOnlyDictionary<string, string> scripts, string name)
    {
        if (!scripts.TryGetValue(name, out var body) || string.IsNullOrWhiteSpace(body))
            return false;
        // Skip the scaffolded npm placeholder test so it never turns the gate red.
        if (body.Contains(NpmDefaultTestMarker, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }
}
