using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace AgentStudio.Git;

/// <summary>
/// Ambient, per-request accounting of git subprocess spawns for the git-info
/// endpoints (status / diff / hygiene / provenance / inventory). This is the
/// MEASURE half of AGT-2007: git-info requests were slow and it was not
/// obvious why, because a single request quietly forks many serial git
/// processes and on Windows a bare spawn already costs ~70-100ms. A request
/// opens a <see cref="BeginRequest"/> scope; every <see cref="GitService"/>
/// spawn records its subcommand and duration into the ambient scope; and the
/// scope logs a rollup on dispose - "how many git processes ran for this
/// request, and where the wall-time went".
///
/// <para>
/// The scope flows through the async/thread-pool boundary via
/// <see cref="AsyncLocal{T}"/>, so a request that fans its independent reads
/// out across the thread pool (see <c>GitService.RunGitParallel</c>) still
/// accounts every spawn against the originating request.
/// </para>
/// </summary>
public static class GitProcessTelemetry
{
    private static readonly AsyncLocal<GitRequestScope?> _current = new();

    /// <summary>
    /// A single git spawn slower than this is logged at Warning even when no
    /// request scope is active, so a pathological command (a huge diff, a
    /// hung fetch) is always visible in the logs.
    /// </summary>
    private const long SlowSpawnWarnMs = 1500;

    /// <summary>
    /// Process-wide logger for out-of-scope slow-spawn warnings. Set once from
    /// the <see cref="GitService"/> constructor; the request rollup uses the
    /// per-request logger passed to <see cref="BeginRequest"/> instead.
    /// </summary>
    internal static ILogger? Logger;

    /// <summary>
    /// Opens a per-request git measurement scope. Dispose logs the rollup:
    /// total spawn count, summed git wall-time, request wall-time, and a
    /// per-subcommand breakdown. Nestable - the inner scope restores the outer
    /// on dispose - though the git-info entry points do not nest in practice.
    /// </summary>
    public static IDisposable BeginRequest(string label, ILogger logger)
    {
        var scope = new GitRequestScope(label, logger, _current.Value);
        _current.Value = scope;
        return scope;
    }

    /// <summary>
    /// Records one completed git spawn against the ambient request scope (a
    /// no-op when nothing is measuring) and warns on a pathologically slow
    /// individual spawn.
    /// </summary>
    internal static void Record(string command, long elapsedMs, int exitCode)
    {
        _current.Value?.Add(command, elapsedMs);
        if (elapsedMs >= SlowSpawnWarnMs)
        {
            Logger?.LogWarning(
                "git slow-spawn command={Command} elapsedMs={ElapsedMs} exit={Exit}",
                command, elapsedMs, exitCode);
        }
    }

    /// <summary>
    /// Diagnostic/test hook: the ambient scope's running tally
    /// (spawn count, summed git ms), or null when nothing is measuring.
    /// </summary>
    internal static (int Spawns, long GitMs)? CurrentTally()
        => _current.Value is { } s ? (s.Spawns, s.GitMs) : null;

    private sealed class GitRequestScope : IDisposable
    {
        private readonly string _label;
        private readonly ILogger _logger;
        private readonly GitRequestScope? _parent;
        private readonly Stopwatch _wall = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly Dictionary<string, (int Count, long Ms)> _byCommand = new(StringComparer.Ordinal);

        public int Spawns { get; private set; }
        public long GitMs { get; private set; }

        public GitRequestScope(string label, ILogger logger, GitRequestScope? parent)
        {
            _label = label;
            _logger = logger;
            _parent = parent;
        }

        public void Add(string command, long elapsedMs)
        {
            lock (_gate)
            {
                Spawns++;
                GitMs += elapsedMs;
                var prev = _byCommand.TryGetValue(command, out var c) ? c : (0, 0L);
                _byCommand[command] = (prev.Item1 + 1, prev.Item2 + elapsedMs);
            }
        }

        public void Dispose()
        {
            _wall.Stop();
            // Restore the outer scope (if any) so nested requests are balanced.
            _current.Value = _parent;

            string breakdown;
            int spawns;
            long gitMs;
            lock (_gate)
            {
                spawns = Spawns;
                gitMs = GitMs;
                breakdown = string.Join(", ", _byCommand
                    .OrderByDescending(kv => kv.Value.Ms)
                    .Select(kv => $"{kv.Key}x{kv.Value.Count}={kv.Value.Ms}ms"));
            }

            // gitMs is the summed subprocess time; when a request fans its reads
            // out in parallel, wallMs is lower than gitMs - that gap is exactly
            // the serial time the parallelism removed.
            _logger.LogInformation(
                "git-info request={Label} spawns={Spawns} gitMs={GitMs} wallMs={WallMs} breakdown=[{Breakdown}]",
                _label, spawns, gitMs, _wall.ElapsedMilliseconds, breakdown);
        }
    }
}
