/**
 * Windows control-plane tunnel keeper + watchdog (AGT-2664).
 *
 * During the interim local-profile topology, a Windows Studio host reaches the
 * Linux agent host only through a supervised reverse SSH tunnel: the
 * `AgentRunner-TunnelKeeper` Scheduled Task owns the SSH forward and the
 * independent `AgentRunner-TunnelWatchdog` Scheduled Task heals it. This model
 * mirrors `GET/POST /api/v1/management/windows-tunnel/*`, backed by the
 * repository-owned scripts under `deploy/windows/agent-runner-tunnel/`.
 */

export type WindowsTunnelPlatform = 'windows' | 'unsupported';

export interface WindowsTunnelTaskStatus {
  taskName: string;
  registered: boolean;
  state: string | null;
  lastRunTime: string | null;
  lastTaskResult: number | null;
  nextRunTime: string | null;
}

export interface WindowsTunnelKeeperHealth {
  status: 'healthy' | 'unreachable' | null;
  message: string | null;
  observedAt: string | null;
  repairAttempts: number | null;
}

export interface WindowsTunnelWatchdogHealth {
  lastHealSucceededAt: string | null;
  lastHealFailedAt: string | null;
  lastProbeFailedAt: string | null;
  lastEvent: string | null;
  lastEventAt: string | null;
}

export interface WindowsTunnelStatus {
  platform: WindowsTunnelPlatform;
  observedAt: string;
  keeperTask: WindowsTunnelTaskStatus | null;
  keeperHealth: WindowsTunnelKeeperHealth | null;
  watchdogTask: WindowsTunnelTaskStatus | null;
  watchdogHealth: WindowsTunnelWatchdogHealth | null;
  alarmActive: boolean;
  detail: string | null;
}

export interface WindowsTunnelRegisterRequest {
  sshTarget: string;
  remotePort: number;
  taskServerPort: number;
  intervalMinutes: number;
  probeIntervalSeconds: number;
  failureThreshold: number;
}

export interface WindowsTunnelRegistrationResponse {
  platform: WindowsTunnelPlatform;
  ok: boolean;
  elevated: boolean;
  detail: string | null;
  requestedAt: string;
}

export const WINDOWS_TUNNEL_DEFAULTS: WindowsTunnelRegisterRequest = {
  sshTarget: 'agent-runner',
  remotePort: 15031,
  taskServerPort: 5031,
  intervalMinutes: 5,
  probeIntervalSeconds: 60,
  failureThreshold: 2,
};

export type WindowsTunnelDisplayState = 'ok' | 'warn' | 'error' | 'not-registered' | 'unsupported' | 'unknown';

/**
 * A single roll-up tone for the keeper+watchdog pair. Acute (`error`) only
 * when the alarm channel is active or a task is missing; `warn` covers a
 * degraded-but-self-healing keeper.
 */
export function windowsTunnelDisplayState(status: WindowsTunnelStatus | null): WindowsTunnelDisplayState {
  if (!status) return 'unknown';
  if (status.platform === 'unsupported') return 'unsupported';
  if (status.alarmActive) return 'error';
  const keeperRegistered = status.keeperTask?.registered ?? false;
  const watchdogRegistered = status.watchdogTask?.registered ?? false;
  if (!keeperRegistered || !watchdogRegistered) return 'not-registered';
  if (status.keeperHealth?.status === 'unreachable') return 'warn';
  return 'ok';
}

export function windowsTunnelDisplayLabel(status: WindowsTunnelStatus | null): string {
  switch (windowsTunnelDisplayState(status)) {
    case 'ok': return 'Registered and healthy';
    case 'warn': return 'Registered, self-healing';
    case 'error': return 'Registered, heal failed twice';
    case 'not-registered': return 'Not registered';
    case 'unsupported': return 'Not applicable on this platform';
    case 'unknown': return 'Unknown';
  }
}

export function windowsTunnelLastHealLabel(status: WindowsTunnelStatus | null): string {
  const watchdog = status?.watchdogHealth;
  if (!watchdog?.lastHealSucceededAt && !watchdog?.lastHealFailedAt) return 'No heal recorded yet';
  const succeededAt = watchdog.lastHealSucceededAt ? Date.parse(watchdog.lastHealSucceededAt) : Number.NaN;
  const failedAt = watchdog.lastHealFailedAt ? Date.parse(watchdog.lastHealFailedAt) : Number.NaN;
  if (Number.isFinite(failedAt) && (!Number.isFinite(succeededAt) || failedAt > succeededAt)) {
    return `Last heal failed at ${watchdog.lastHealFailedAt}`;
  }
  return `Last heal succeeded at ${watchdog.lastHealSucceededAt}`;
}
