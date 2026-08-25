import type { VisibleCliTaskRequest } from '../../visible-cli-task';
import { isLocalUrl } from '../../task-server';
import type { RemoteHost } from './remote-host.model';

export type RunnerSetupConnectionMode = 'central' | 'lan' | 'tunnel';

export interface RunnerSetupConfig {
  sshTarget: string;
  taskServerUrl: string;
  connectionMode: RunnerSetupConnectionMode | '';
  clientId: string;
  gitRemote: string;
  gitPushRemote: string;
  tunnelDevspacePath: string;
  orchestratorPort: number;
  tunnelRegistrationConsent: boolean;
}

/** Validate the operator-owned values before a provisioning task can be queued. */
export function runnerSetupIssues(config: RunnerSetupConfig): string[] {
  const issues: string[] = [];
  if (!config.sshTarget.trim()) issues.push('SSH target is required.');
  if (!config.taskServerUrl.trim()) {
    issues.push('Task Server URL is required.');
  } else if (!isHttpUrl(config.taskServerUrl)) {
    issues.push('Task Server URL must be an absolute HTTP or HTTPS URL.');
  }
  if (!config.connectionMode) issues.push('Choose how the remote host reaches the Task Server.');
  if (!config.clientId.trim()) issues.push('Client identity is required.');
  if (!config.gitRemote.trim()) issues.push('Git remote URL is required.');
  if (!config.gitPushRemote.trim()) issues.push('Git push URL is required.');
  if (config.taskServerUrl.trim() && isLocalUrl(config.taskServerUrl) && config.connectionMode !== 'tunnel') {
    issues.push('A remote host cannot reach this loopback URL. Choose Tunnel or enter a central or LAN URL.');
  }
  if (config.connectionMode === 'tunnel') {
    if (!config.tunnelDevspacePath.trim()) {
      issues.push('Windows devspace path is required for tunnel supervision.');
    }
    if (!Number.isInteger(config.orchestratorPort) || config.orchestratorPort < 1 || config.orchestratorPort > 65_535) {
      issues.push('Local Task Server port must be between 1 and 65535.');
    }
    if (tunnelRemotePort(config.taskServerUrl) === null) {
      issues.push('Tunnel Task Server URL must include the remote listener port.');
    }
    if (!config.tunnelRegistrationConsent) {
      issues.push('Confirm the one-time Windows administrator registration.');
    }
  }
  return issues;
}

/** Build the durable CLI task that owns progress, operator input, and history. */
export function buildRunnerSetupRequest(host: RemoteHost, config: RunnerSetupConfig): VisibleCliTaskRequest {
  const sshTarget = config.sshTarget.trim();
  const taskServerUrl = config.taskServerUrl.trim().replace(/\/+$/, '');
  const clientId = config.clientId.trim();
  const gitRemote = config.gitRemote.trim();
  const gitPushRemote = config.gitPushRemote.trim();
  const connectionMode = config.connectionMode || 'not selected';
  const healthUrl = `${taskServerUrl}/healthz`;
  const remotePort = tunnelRemotePort(taskServerUrl);
  const controllerCommand = [
    'bash scripts/remote-runner-onboard.sh',
    `--host ${shellArg(sshTarget)}`,
    `--server ${shellArg(taskServerUrl)}`,
    `--topology ${shellArg(connectionMode)}`,
    `--client-id ${shellArg(clientId)}`,
    `--runner-name ${shellArg(host.name)}`,
    `--git-remote ${shellArg(gitRemote)}`,
    `--git-push-remote ${shellArg(gitPushRemote)}`,
  ].join(' ');
  const tunnelSetupCommand = connectionMode === 'tunnel' && remotePort !== null
    ? [
        'powershell.exe -NoProfile -ExecutionPolicy Bypass',
        `-File ${powerShellArg('.\\deploy\\windows\\agent-runner-tunnel\\setup-tunnel-supervision.ps1')}`,
        `-SshTarget ${powerShellArg(sshTarget)}`,
        `-RemotePort ${remotePort}`,
        `-OrchestratorPort ${config.orchestratorPort}`,
        `-DevspacePath ${powerShellArg(config.tunnelDevspacePath.trim())}`,
        '-Force',
      ].join(' ')
    : null;
  const executionCommand = tunnelSetupCommand
    ? `${tunnelSetupCommand} && ${controllerCommand}`
    : controllerCommand;
  const tunnelPrompt = tunnelSetupCommand
    ? [
        '0. Windows control-plane tunnel supervision (must run before the remote reachability gate)',
        '- The operator explicitly consented in Studio to the one-time administrator registration. Run the product-owned command below on the Windows control-plane machine. Do not replace it with direct Scheduled Task commands.',
        `- Run: \`${tunnelSetupCommand}\``,
        '- Windows will show a UAC prompt. Wait for the operator to approve or decline it. Never attempt to bypass UAC or weaken the Scheduled Task principal.',
        '- Require both keeper and watchdog to report `registered=True`. If elevation is declined or registration fails, stop and report the transcript path printed by the wrapper.',
        '- The wrapper writes the supervision snapshot used by Workspace Settings -> Execution Hosts. Registration is complete only after that snapshot is written.',
        '',
      ]
    : [];

  return {
    title: `Set up agent host on ${host.name}`,
    scope: `Set up the agent host daemon on ${host.name}`,
    reason: 'Provision the host idempotently, verify its Studio-provisioned provider environment, register its agent-host daemon, and prove one real remote task handoff.',
    command: executionCommand,
    expectedDuration: '10 to 20 minutes',
    cliType: 'codex',
    context: {
      host: host.name,
      sshTarget,
      taskServerUrl,
      connectionMode,
      clientId,
      gitRemote,
      gitPushRemote,
      executionBoundary: tunnelSetupCommand
        ? 'Register tunnel supervision on the Windows control plane after explicit UAC consent; perform every agent-host operation through SSH.'
        : 'Run the controller agent locally; perform every host operation through SSH.',
    },
    prompt: [
      `Set up remote host ${host.name}. ${tunnelSetupCommand ? 'The consented tunnel-supervision step runs on the Windows control plane; every agent-host inspection and mutation runs' : 'Run every inspection and mutation'} through SSH target \`${sshTarget}\`. Do not install or configure agent-host on the operator workstation.`,
      '',
      'Treat this as an idempotent remote-host process that is safe to repeat after a wipe. Report each phase in the task conversation and fail with a concrete recovery instruction instead of waiting silently.',
      '',
      ...tunnelPrompt,
      '1. Reachability gate (must run first)',
      `- From the remote host, verify \`${healthUrl}\` with curl and the exact header \`X-Client-Id: ${clientId}\`.`,
      `- Connection mode is \`${connectionMode}\`. If it is \`tunnel\`, verify the tunnel on the remote host before curl.`,
      '- Do not install or start agent-host until this remote curl succeeds. On failure, show whether the operator needs a central URL, LAN binding, or tunnel.',
      '',
      '2. Agent host and source setup',
      `- Run the product-owned controller exactly as recorded in the Execution contract: \`${controllerCommand}\`. Do not replace it with hand-written local installation steps.`,
      '- Confirm .NET 10 is available.',
      '- Install or update the NuGet global tool `CodingAgentRunner` with a version range of `[0.5.0,)`; verify the resolved version is at least 0.5.0.',
      '- The controller intentionally fails if the published NuGet package is not a DotnetTool. Report that packaging failure; do not bypass it with a session process or copied binary.',
      `- Configure agent-host with Task Server URL \`${taskServerUrl}\` and client identity \`${clientId}\` so every request sends that exact X-Client-Id.`,
      `- Preserve the existing runner identity \`${host.name}\`; do not rename historical runner ids during the daemon migration.`,
      `- Configure fetches from \`${gitRemote}\` and pushes through the host-owned write identity at \`${gitPushRemote}\`; do not transfer workstation credentials.`,
      '- Create or update `agent-host.service`, keep the `agent-runner.service` compatibility alias, run daemon-reload, enable it, and start it through systemd. Never leave agent-host as a shell, tmux, nohup, or user-session process.',
      '',
      '3. Provider authentication contract',
      '- Install Codex and Claude CLI on the host when missing, then report their versions.',
      '- Confirm `/etc/agent-runner/provider-auth.env` exists with mode `640` and ownership `root:agent`. Report variable names only and never print values.',
      '- Confirm both `agent-runner.service` and `agent-runner-review.service`, when installed, load that shared file with `EnvironmentFile=` after their role-specific EnvironmentFile entry.',
      '- Provider credentials were already delivered by Studio through SSH stdin. Never request, repeat, or place a credential in this task conversation, command arguments, repository, or task files.',
      '- After restart, verify through `/proc/<main-pid>/environ` that the expected variable name reached the daemon, without printing its value.',
      '- Treat the runner provider probe as authoritative. It reads process environment only, never a credential file path. Report each `runner-provider-auth` result and the matching capability snapshot detail.',
      '',
      '4. Verification and real handoff',
      '- Confirm the systemd service is active and enabled, the Task Server accepts the configured client identity, and the client registry shows a fresh LastSeen plus runnerGitStatus `ready`.',
      '- Run the agent-host connection or health check and include the result.',
      '- Queue and complete one real smoke task through this remote host. Prove the remote lease/runner attribution and final task result; a local or synthetic no-op is not acceptance.',
      '- Finish with the installed agent-host version, service state, Task Server reachability result, provider-auth probe results, refreshed LastSeen, and smoke-task key/result.',
    ].join('\n'),
  };
}

function isHttpUrl(value: string): boolean {
  try {
    const url = new URL(value);
    return url.protocol === 'http:' || url.protocol === 'https:';
  } catch {
    return false;
  }
}

function shellArg(value: string): string {
  return `'${value.replace(/'/g, `'"'"'`)}'`;
}

function powerShellArg(value: string): string {
  return `'${value.replace(/'/g, "''")}'`;
}

function tunnelRemotePort(value: string): number | null {
  try {
    const url = new URL(value.trim());
    if (!url.port) return null;
    const port = Number(url.port);
    return Number.isInteger(port) && port >= 1 && port <= 65_535 ? port : null;
  } catch {
    return null;
  }
}
