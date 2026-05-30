using OrchestratorApi.Models;

namespace OrchestratorApi.Services.Cli;

/// <summary>
/// Dispatches calls to the right <see cref="ICliExecutionService"/> based on
/// <see cref="CliTypes"/>. Re-broadcasts each backend's events so consumers
/// (SignalR hub, runner) only need a single subscription.
/// </summary>
public sealed class CliRouter
{
    private readonly Dictionary<string, ICliExecutionService> _byType;

    public event Action<string, string, CliOutputLine>? OnOutput;          // (cliType, jobKey, line)
    public event Action<string, string, CliExecution>?  OnStarted;
    public event Action<string, string, CliExecution>?  OnFinished;
    public event Action<string, string, CliRunEvent>?   OnRunEvent;        // (cliType, jobKey, event)

    public CliRouter(
        CopilotCliService copilot,
        ClaudeCliService claude,
        CodexCliService codex,
        GeminiCliService gemini)
    {
        _byType = new(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Copilot] = copilot,
            [CliTypes.Claude]  = claude,
            [CliTypes.Codex]   = codex,
            [CliTypes.Gemini]  = gemini,
        };

        foreach (var (type, svc) in _byType)
        {
            svc.OnOutput   += (jobKey, line) => OnOutput?.Invoke(type, jobKey, line);
            svc.OnStarted  += (jobKey, exec) => OnStarted?.Invoke(type, jobKey, exec);
            svc.OnFinished += (jobKey, exec) => OnFinished?.Invoke(type, jobKey, exec);
            svc.OnRunEvent += (jobKey, evt)  => OnRunEvent?.Invoke(type, jobKey, evt);
        }
    }

    public IEnumerable<ICliExecutionService> All => _byType.Values;

    public ICliExecutionService Get(string? cliType)
        => _byType.TryGetValue(CliTypes.Normalize(cliType), out var svc)
            ? svc
            // Fallback for an unknown/unset cli is Claude, not Copilot: Claude is
            // the project default and always has plan quota, whereas defaulting to
            // Copilot sent every cli-less task into a 402 "no quota" retry loop
            // that pinned the task in 3-progress and stalled the whole queue.
            : _byType.TryGetValue(CliTypes.Claude, out var claude) ? claude
            : _byType[CliTypes.Copilot];

    public void ReattachAll()
    {
        foreach (var svc in _byType.Values) svc.ReattachOnStartup();
    }
}
