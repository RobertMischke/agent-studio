import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import type { RemoteHost, RemoteHostCapabilityHealth } from './remote-host.model';
import { providerAuthViews, providerAuthWaitReason } from './provider-auth.model';

const NOW = Date.parse('2026-08-04T12:00:00Z');

describe('provider auth projection', () => {
  it('maps ready, unavailable, and stale probe status without guessing', () => {
    const host = remoteHost([
      capability('cli-execution:claude'),
      capability('provider-auth:claude'),
      capability('cli-execution:codex'),
      capability('provider-auth:codex', 'unavailable', true, 'Not logged in'),
      capability('cli-execution:gemini'),
      capability('provider-auth:gemini', 'ready', false),
    ]);

    expect(providerAuthViews(host, NOW).map(view => [view.provider, view.state])).toEqual([
      ['claude', 'ok'],
      ['codex', 'unavailable'],
      ['gemini', 'unknown'],
    ]);
  });

  it('names the assigned host when a ready remote card waits for sign-in', () => {
    const host = remoteHost([
      capability('cli-execution:claude'),
      capability('provider-auth:claude', 'unavailable', true, 'Not logged in'),
    ]);
    const task = {
      state: '2-ready',
      cliType: 'claude',
      executionLocation: {
        state: 'queued-remote',
        configuredRunnerId: 'runner-a',
      },
    } as TaskInfo;

    expect(providerAuthWaitReason(task, [host], NOW)).toMatchObject({
      label: 'Waiting for Claude Code sign-in on agent-runner-01',
      detail: 'agent-runner-01: Not logged in',
    });
  });

  it('does not show an auth wait when any matching runner is usable', () => {
    const unavailable = remoteHost([
      capability('cli-execution:claude'),
      capability('provider-auth:claude', 'unavailable'),
    ]);
    const ready = { ...remoteHost([
      capability('cli-execution:claude'),
      capability('provider-auth:claude'),
    ]), id: 'runner-b', clientId: 'runner-b', name: 'agent-runner-02' };
    const task = {
      state: '2-ready',
      cliType: 'claude',
      executionLocation: { state: 'queued-remote' },
    } as TaskInfo;

    expect(providerAuthWaitReason(task, [unavailable, ready], NOW)).toBeNull();
  });
});

function capability(
  key: string,
  advertisedStatus = 'ready',
  isFresh = true,
  detail = 'active session',
): RemoteHostCapabilityHealth {
  return {
    key,
    category: key.split(':')[0],
    advertisedStatus,
    healthState: 'healthy',
    advertisedAt: '2026-08-04T11:59:00Z',
    freshUntil: '2026-08-04T12:04:00Z',
    isFresh,
    consecutiveFailures: 0,
    detail,
    affectedClaims: [],
    recoveryHistory: [],
  };
}

function remoteHost(capabilityHealth: RemoteHostCapabilityHealth[]): RemoteHost {
  return {
    id: 'runner-a',
    clientId: 'runner-a',
    name: 'agent-runner-01',
    role: 'remote',
    address: null,
    status: 'online',
    os: 'Linux',
    lastHeartbeatAt: '2026-08-04T11:59:30Z',
    uptimeLabel: null,
    capabilities: [],
    cliQuotas: [],
    stats: null,
    capabilityHealth,
  };
}
