using Microsoft.AspNetCore.SignalR;

namespace OrchestratorApi.Hubs;

public class JobHub : Hub
{
    // Client methods:
    // - jobsChanged                                          → board refresh
    // - cliOutput(jobId, line, stream, timestamp)            → live CLI output line
    // - cliStarted(jobId, processId, startedAt)              → CLI process started
    // - cliFinished(jobId, exitCode, duration, status)       → CLI process finished
    // - runnerStatusChanged(projectName, mode, activeJobId)  → runner mode/status change
    // - busMessageAdded(AgentMessage)                        → new bus event appended
}
