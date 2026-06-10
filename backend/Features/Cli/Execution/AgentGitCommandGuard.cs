using System.Diagnostics;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Worker CLI subprocess guard for the platform-owned git boundary.
/// The backend's own <c>GitService</c> calls do not use this environment.
/// </summary>
internal static class AgentGitCommandGuard
{
    internal const string AllowEnv = "AGENT_TASKBOARD_ALLOW_AGENT_GIT_MUTATION";
    internal const string RealGitEnv = "AGENT_TASKBOARD_REAL_GIT";
    internal const string GuardDirEnv = "AGENT_TASKBOARD_GIT_GUARD_DIR";

    private static readonly string[] ForbiddenCommands =
    [
        "commit",
        "push",
        "commit-tree",
        "tag",
        "reset",
        "checkout",
        "switch",
        "branch",
        "restore",
        "clean",
        "stash",
        "notes"
    ];

    private static readonly string[] GlobalOptionsWithValue =
    [
        "-C",
        "-c",
        "--git-dir",
        "--work-tree",
        "--namespace",
        "--exec-path"
    ];

    public static void Apply(ProcessStartInfo psi)
    {
        if (psi.Environment.TryGetValue(AllowEnv, out var allow)
            && string.Equals(allow, "1", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var realGit = ResolveRealGitExecutable();
        if (string.IsNullOrWhiteSpace(realGit)) return;

        var guardDir = EnsureGuardDirectory();
        if (string.IsNullOrWhiteSpace(guardDir)) return;

        var existingPath = psi.Environment.TryGetValue("PATH", out var path)
            ? path
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        psi.Environment[RealGitEnv] = realGit;
        psi.Environment[GuardDirEnv] = guardDir;
        psi.Environment["PATH"] = guardDir + Path.PathSeparator + existingPath;
    }

    internal static bool IsForbiddenGitCommand(IReadOnlyList<string> args)
    {
        var command = ResolveGitCommand(args);
        return command != null
               && ForbiddenCommands.Any(c => string.Equals(c, command, StringComparison.OrdinalIgnoreCase));
    }

    private static string? ResolveGitCommand(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg)) continue;

            if (GlobalOptionsWithValue.Any(o => string.Equals(o, arg, StringComparison.OrdinalIgnoreCase)))
            {
                i++;
                continue;
            }

            if (GlobalOptionsWithValue.Any(o => arg.StartsWith(o + "=", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (arg.StartsWith("-", StringComparison.Ordinal)) continue;
            return arg;
        }

        return null;
    }

    private static string? ResolveRealGitExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, "git" + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }

        if (OperatingSystem.IsWindows())
        {
            var common = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe")
            };
            foreach (var candidate in common)
            {
                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static string? EnsureGuardDirectory()
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "agent-taskboard-git-guard");
            Directory.CreateDirectory(dir);

            if (OperatingSystem.IsWindows())
            {
                var cmdPath = Path.Combine(dir, "git.cmd");
                if (!File.Exists(cmdPath) || !File.ReadAllText(cmdPath).Contains(AllowEnv, StringComparison.Ordinal))
                {
                    File.WriteAllText(cmdPath, WindowsWrapper);
                }
            }
            else
            {
                var shPath = Path.Combine(dir, "git");
                if (!File.Exists(shPath) || !File.ReadAllText(shPath).Contains(AllowEnv, StringComparison.Ordinal))
                {
                    File.WriteAllText(shPath, PosixWrapper.Replace("\r\n", "\n"));
                    TryMarkExecutable(shPath);
                }
            }

            return dir;
        }
        catch
        {
            return null;
        }
    }

    private static void TryMarkExecutable(string path)
    {
        try
        {
#pragma warning disable CA1416
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
#pragma warning restore CA1416
        }
        catch (Exception __ex)
        {
            SilentCatch.Note(__ex, "AgentGitCommandGuard: Best effort. Some filesystems ignore Unix mode bits.");
            // Best effort. Some filesystems ignore Unix mode bits.
        }
    }

    private const string WindowsWrapper = """
@echo off
setlocal
set "_cmd="
set "_skip="
for %%A in (%*) do (
  if defined _skip (
    set "_skip="
  ) else if /I "%%~A"=="-C" (
    set "_skip=1"
  ) else if /I "%%~A"=="-c" (
    set "_skip=1"
  ) else if /I "%%~A"=="--git-dir" (
    set "_skip=1"
  ) else if /I "%%~A"=="--work-tree" (
    set "_skip=1"
  ) else if /I "%%~A"=="--namespace" (
    set "_skip=1"
  ) else if /I "%%~A"=="--exec-path" (
    set "_skip=1"
  ) else if not defined _cmd (
    echo %%~A | findstr /B /C:"-" >nul
    if errorlevel 1 set "_cmd=%%~A"
  )
)
if /I "%AGENT_TASKBOARD_ALLOW_AGENT_GIT_MUTATION%"=="1" goto run
if /I "%_cmd%"=="commit" goto block
if /I "%_cmd%"=="push" goto block
if /I "%_cmd%"=="commit-tree" goto block
if /I "%_cmd%"=="tag" goto block
if /I "%_cmd%"=="reset" goto block
if /I "%_cmd%"=="checkout" goto block
if /I "%_cmd%"=="switch" goto block
if /I "%_cmd%"=="branch" goto block
if /I "%_cmd%"=="restore" goto block
if /I "%_cmd%"=="clean" goto block
if /I "%_cmd%"=="stash" goto block
if /I "%_cmd%"=="notes" goto block
goto run
:block
echo agent-taskboard git guard: worker agents must not run git %_cmd%; the platform owns commit and push. 1>&2
exit /b 86
:run
"%AGENT_TASKBOARD_REAL_GIT%" %*
exit /b %ERRORLEVEL%
""";

    private const string PosixWrapper = """
#!/bin/sh
cmd=""
skip=""
for arg in "$@"; do
  if [ -n "$skip" ]; then
    skip=""
    continue
  fi
  case "$arg" in
    -C|-c|--git-dir|--work-tree|--namespace|--exec-path)
      skip=1
      continue
      ;;
    --git-dir=*|--work-tree=*|--namespace=*|--exec-path=*)
      continue
      ;;
    -*)
      continue
      ;;
    *)
      cmd="$arg"
      break
      ;;
  esac
done

if [ "$AGENT_TASKBOARD_ALLOW_AGENT_GIT_MUTATION" != "1" ]; then
  case "$cmd" in
    commit|push|commit-tree|tag|reset|checkout|switch|branch|restore|clean|stash|notes)
      echo "agent-taskboard git guard: worker agents must not run git $cmd; the platform owns commit and push." >&2
      exit 86
      ;;
  esac
fi

exec "$AGENT_TASKBOARD_REAL_GIT" "$@"
""";
}
