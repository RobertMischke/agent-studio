using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Cli.Rendering;

/// <summary>
/// Translates one raw CLI stdout/stderr line into zero or more human-readable
/// marker lines (e.g. <c>● Read /path/to/file</c>) that the frontend's
/// activity-log parser already knows how to classify.
///
/// <para>
/// <b>Why this is its own strategy interface (ADR-0013 marker-line twin).</b>
/// The typed-event path already factors per-CLI frame mapping into pure static
/// <c>*EventAdapter</c> classes (<see cref="Adapters.ClaudeEventAdapter"/>,
/// <see cref="Adapters.CodexEventAdapter"/>). This interface is the marker-line
/// equivalent: a pure, dependency-free transform that the driver delegates to
/// from <c>TransformReadLine</c>. Keeping it out of the driver means a renderer
/// can be unit-tested per frame with a plain <c>new XxxOutputRenderer()</c> -
/// no <see cref="CodexCliService"/>-style constructor graph, no process, no
/// configuration - and a new CLI plugs in by implementing this one method.
/// </para>
///
/// <para>
/// Implementations MUST be pure over a single line: no side effects, no state
/// across calls. Session-id / telemetry capture is a side effect on
/// <c>ProcInfo</c> and stays in the driver's <c>OnOutputLine</c> /
/// <c>MapLineToRunEvents</c> hooks, never here.
/// </para>
/// </summary>
public interface ICliOutputRenderer
{
    /// <summary>
    /// Map one raw line to zero or more rendered lines. The default contract:
    /// non-stdout, blank, or non-JSON lines pass through unchanged; recognised
    /// JSON frames render to marker lines; unrecognised frame types render to a
    /// <c>● &lt;type&gt;</c> catch-all so raw JSON never leaks into the log.
    /// </summary>
    IEnumerable<CliOutputLine> Render(CliOutputLine raw);
}
