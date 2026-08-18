import { describe, expect, it } from 'vitest';
import {
  windowsTunnelDisplayLabel,
  windowsTunnelDisplayState,
  windowsTunnelLastHealLabel,
  type WindowsTunnelStatus,
} from './windows-tunnel.model';

describe('windowsTunnelDisplayState', () => {
  it('reads unknown when no status has loaded yet', () => {
    expect(windowsTunnelDisplayState(null)).toBe('unknown');
  });

  it('reads unsupported on a non-Windows Studio host', () => {
    expect(windowsTunnelDisplayState(status({ platform: 'unsupported' }))).toBe('unsupported');
  });

  it('reads not-registered when either scheduled task is missing', () => {
    expect(windowsTunnelDisplayState(status({ keeperTask: task(false) }))).toBe('not-registered');
    expect(windowsTunnelDisplayState(status({ watchdogTask: task(false) }))).toBe('not-registered');
  });

  it('reads ok when both tasks are registered and the keeper is healthy', () => {
    expect(windowsTunnelDisplayState(status({}))).toBe('ok');
  });

  it('reads warn when the keeper is mid-repair but the watchdog has not alarmed', () => {
    expect(windowsTunnelDisplayState(status({
      keeperHealth: { status: 'unreachable', message: 'repairing', observedAt: null, repairAttempts: 1 },
    }))).toBe('warn');
  });

  it('reads error (acute) once the watchdog alarm channel is active, even if the keeper looks healthy', () => {
    expect(windowsTunnelDisplayState(status({ alarmActive: true }))).toBe('error');
  });
});

describe('windowsTunnelDisplayLabel', () => {
  it('renders a human label for every state', () => {
    expect(windowsTunnelDisplayLabel(null)).toBe('Unknown');
    expect(windowsTunnelDisplayLabel(status({}))).toBe('Registered and healthy');
    expect(windowsTunnelDisplayLabel(status({ alarmActive: true }))).toBe('Registered, heal failed twice');
    expect(windowsTunnelDisplayLabel(status({ platform: 'unsupported' }))).toBe('Not applicable on this platform');
  });
});

describe('windowsTunnelLastHealLabel', () => {
  it('reports no heal recorded when the watchdog has never healed', () => {
    expect(windowsTunnelLastHealLabel(status({
      watchdogHealth: {
        lastHealSucceededAt: null, lastHealFailedAt: null, lastProbeFailedAt: null,
        lastEvent: null, lastEventAt: null,
      },
    }))).toBe('No heal recorded yet');
  });

  it('prefers the most recent event between a success and a later failure', () => {
    expect(windowsTunnelLastHealLabel(status({
      watchdogHealth: {
        lastHealSucceededAt: '2026-08-18T08:00:00Z',
        lastHealFailedAt: '2026-08-18T09:00:00Z',
        lastProbeFailedAt: null, lastEvent: 'heal_failed', lastEventAt: '2026-08-18T09:00:00Z',
      },
    }))).toContain('failed at 2026-08-18T09:00:00Z');
  });

  it('reports the succeeded timestamp when it is the latest event', () => {
    expect(windowsTunnelLastHealLabel(status({
      watchdogHealth: {
        lastHealSucceededAt: '2026-08-18T09:00:00Z',
        lastHealFailedAt: '2026-08-18T08:00:00Z',
        lastProbeFailedAt: null, lastEvent: 'heal_succeeded', lastEventAt: '2026-08-18T09:00:00Z',
      },
    }))).toContain('succeeded at 2026-08-18T09:00:00Z');
  });
});

function task(registered: boolean) {
  return {
    taskName: 'AgentRunner-TunnelKeeper',
    registered,
    state: registered ? 'Ready' : null,
    lastRunTime: null,
    lastTaskResult: null,
    nextRunTime: null,
  };
}

function status(overrides: Partial<WindowsTunnelStatus>): WindowsTunnelStatus {
  return {
    platform: 'windows',
    observedAt: '2026-08-18T09:00:00Z',
    keeperTask: task(true),
    keeperHealth: { status: 'healthy', message: null, observedAt: null, repairAttempts: 0 },
    watchdogTask: { ...task(true), taskName: 'AgentRunner-TunnelWatchdog' },
    watchdogHealth: {
      lastHealSucceededAt: null, lastHealFailedAt: null, lastProbeFailedAt: null,
      lastEvent: null, lastEventAt: null,
    },
    alarmActive: false,
    detail: null,
    ...overrides,
  };
}
