/**
 * Windows control-plane host setup (AGT-2664): status of the two Scheduled
 * Tasks `install-tunnel-supervision.ps1` registers - the tunnel keeper and
 * its watchdog - read from `GET /api/v1/windows-tunnel-supervision/status`.
 * Registering the tasks needs one elevated session; this status is read-only
 * and needs none.
 */
export type ScheduledTaskPresence =
  | 'notApplicable'
  | 'notRegistered'
  | 'registered'
  | 'running'
  | 'disabled'
  | 'unknown';

export interface ScheduledTaskStatus {
  taskName: string;
  presence: ScheduledTaskPresence;
  lastRunResult: string | null;
  lastRunAt: string | null;
}

/** Wire shape returned by GET /api/v1/windows-tunnel-supervision/status. */
export interface WindowsTunnelSupervisionStatus {
  isWindowsHost: boolean;
  keeper: ScheduledTaskStatus;
  watchdog: ScheduledTaskStatus;
  lastHealAt: string | null;
  lastHealDetail: string | null;
  consecutiveHealFailures: number;
  detail: string;
}

// ---------------------------------------------------------------------------
// Pure display helpers - co-located with the types so the component and its
// spec share one source of truth. Side-effect free.
// ---------------------------------------------------------------------------

export type ScheduledTaskTone = 'ok' | 'idle' | 'warn' | 'error' | 'calm';

/** Only "not registered" is acute (R4): everything else is calm or working. */
export function scheduledTaskTone(presence: ScheduledTaskPresence): ScheduledTaskTone {
  switch (presence) {
    case 'running': return 'ok';
    case 'registered': return 'idle';
    case 'notRegistered': return 'error';
    case 'disabled': return 'warn';
    case 'notApplicable': return 'calm';
    case 'unknown': return 'calm';
  }
}

export function scheduledTaskLabel(presence: ScheduledTaskPresence): string {
  switch (presence) {
    case 'running': return 'Running';
    case 'registered': return 'Registered';
    case 'notRegistered': return 'Not registered';
    case 'disabled': return 'Disabled';
    case 'notApplicable': return 'Not applicable';
    case 'unknown': return 'Unknown';
  }
}

/** Whether the guided "Set up tunnel supervision" action should be offered. */
export function needsTunnelSupervisionSetup(status: WindowsTunnelSupervisionStatus): boolean {
  return status.isWindowsHost
    && (status.keeper.presence === 'notRegistered' || status.watchdog.presence === 'notRegistered');
}
