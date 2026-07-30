namespace AgentStudio.Setup;

internal sealed record InstallPaths(
    string OrchestratorOpt,
    string OrchestratorConfig,
    string OrchestratorState,
    string StudioOpt,
    string HostOpt,
    string HostConfig,
    string HostState,
    string Systemd)
{
    public static InstallPaths Load()
        => new(
            Environment.GetEnvironmentVariable("AGENT_SETUP_ORCHESTRATOR_OPT")
            ?? "/opt/agent-orchestrator",
            Environment.GetEnvironmentVariable("AGENT_SETUP_ORCHESTRATOR_CONFIG")
            ?? "/etc/agent-orchestrator",
            Environment.GetEnvironmentVariable("AGENT_SETUP_ORCHESTRATOR_STATE")
            ?? "/var/lib/agent-orchestrator",
            Environment.GetEnvironmentVariable("AGENT_SETUP_STUDIO_OPT")
            ?? "/opt/agent-studio",
            Environment.GetEnvironmentVariable("AGENT_SETUP_HOST_OPT")
            ?? "/opt/agent-host",
            Environment.GetEnvironmentVariable("AGENT_SETUP_HOST_CONFIG")
            ?? "/etc/agent-host",
            Environment.GetEnvironmentVariable("AGENT_SETUP_HOST_STATE")
            ?? "/var/lib/agent-host",
            Environment.GetEnvironmentVariable("AGENT_SETUP_SYSTEMD_ROOT")
            ?? "/etc/systemd/system");
}
