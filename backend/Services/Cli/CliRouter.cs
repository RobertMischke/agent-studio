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

    public CliRouter(
        CopilotCliService copilot,
        ClaudeCliService claude,
        CodexCliService codex)
    {
        _byType = new(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Copilot] = copilot,
            [CliTypes.Claude]  = claude,
            [CliTypes.Codex]   = codex,
        };

        foreach (var (type, svc) in _byType)
        {
            svc.OnOutput   += (jobKey, line) => OnOutput?.Invoke(type, jobKey, line);
            svc.OnStarted  += (jobKey, exec) => OnStarted?.Invoke(type, jobKey, exec);
            svc.OnFinished += (jobKey, exec) => OnFinished?.Invoke(type, jobKey, exec);
        }
    }

    public IEnumerable<ICliExecutionService> All => _byType.Values;

    public ICliExecutionService Get(string? cliType)
        => _byType.TryGetValue(CliTypes.Normalize(cliType), out var svc)
            ? svc
            : _byType[CliTypes.Copilot];

    public void ReattachAll()
    {
        foreach (var svc in _byType.Values) svc.ReattachOnStartup();
    }
}
