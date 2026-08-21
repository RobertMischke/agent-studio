/**
 * Windows control-plane host tunnel keeper/watchdog visibility (AGT-2664).
 * Mirrors `GET /api/system/tunnel-supervision`, sourced from the status file
 * `deploy/windows/agent-runner-tunnel/setup-tunnel-supervision.ps1` writes.
 * `snapshot` is null on every deployment that has never run the guided
 * Windows registration - the overwhelming majority.
 */
export type TunnelSupervisionOverall = 'not-configured' | 'healthy' | 'attention' | 'stale';

export interface TunnelKeeperStatus {
  readonly taskName: string;
  readonly registered: boolean;
  readonly state: string | null;
  readonly lastStatus: string | null;
  readonly lastObservedAt: string | null;
  readonly lastMessage: string | null;
}

export interface TunnelWatchdogStatus {
  readonly taskName: string;
  readonly registered: boolean;
  readonly state: string | null;
  readonly lastProbeAt: string | null;
  readonly lastProbeResult: string | null;
  readonly lastHealAt: string | null;
  readonly lastHealResult: string | null;
  readonly consecutiveProbeFailures: number | null;
}

export interface TunnelSupervisionSnapshot {
  readonly schemaVersion: number;
  readonly generatedAt: string;
  readonly keeper: TunnelKeeperStatus;
  readonly watchdog: TunnelWatchdogStatus;
}

export interface TunnelSupervisionResponse {
  readonly overall: TunnelSupervisionOverall;
  readonly snapshot: TunnelSupervisionSnapshot | null;
}

export type TunnelSupervisionTone = 'ok' | 'warn' | 'error' | 'idle';

export function tunnelSupervisionTone(overall: TunnelSupervisionOverall): TunnelSupervisionTone {
  switch (overall) {
    case 'healthy': return 'ok';
    case 'attention': return 'error';
    case 'stale': return 'warn';
    case 'not-configured': return 'idle';
  }
}
