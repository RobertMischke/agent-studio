import { describe, expect, it } from 'vitest';
import type { TaskInfo } from '../../../models/task.model';
import {
  providerAuthBadgesForSnapshot,
  providerAuthWaitReason,
} from './provider-auth.model';
import type { TaskServerRunnerCapabilitySnapshot } from './remote-host.model';

const NOW = Date.parse('2026-08-04T12:00:00Z');

describe('provider auth projection', () => {
  it('maps fresh probe truth to OK, unavailable, and unknown badges with detail', () => {
    const ok = providerAuthBadgesForSnapshot(snapshot('ready', 'healthy', true), NOW)[0];
    const unavailable = providerAuthBadgesForSnapshot(
      snapshot('unavailable', 'healthy', true, 'Not logged in'),
      NOW,
    )[0];
    const unknown = providerAuthBadgesForSnapshot(snapshot('ready', 'healthy', false), NOW)[0];

    expect(ok.state).toBe('ok');
    expect(unavailable.state).toBe('unavailable');
    expect(unavailable.detail).toContain('Not logged in');
    expect(unknown.state).toBe('unknown');
    expect(unknown.detail).toContain('expired');
  });

  it('warns fourteen days before a known credential expiry', () => {
    const expiresAt = new Date(NOW + 13 * 24 * 60 * 60_000).toISOString();
    const badge = providerAuthBadgesForSnapshot(
      snapshot('ready', 'healthy', true, undefined, expiresAt),
      NOW,
    )[0];

    expect(badge.expiresSoon).toBe(true);
    expect(badge.expiryLabel).toBe('Expires in 13 days');
  });

  it('keeps transient and expiring conditions claimable while showing their distinct state', () => {
    const transient = providerAuthBadgesForSnapshot(
      snapshot('ready', 'suspect', true, 'transient auth error, retrying', null, 'transient-auth-error'),
      NOW,
    )[0];
    const expiring = providerAuthBadgesForSnapshot(
      snapshot(
        'ready',
        'healthy',
        true,
        'credentials expiring',
        new Date(NOW + 10 * 24 * 60 * 60_000).toISOString(),
        'credentials-expiring',
      ),
      NOW,
    )[0];

    expect(transient.state).toBe('retrying');
    expect(transient.stateLabel).toBe('transient auth error, retrying');
    expect(expiring.state).toBe('expiring');
    expect(expiring.stateLabel).toBe('credentials expiring');
  });

  it('labels only a confirmed sign-out as re-auth needed', () => {
    const signedOut = providerAuthBadgesForSnapshot(
      snapshot('unavailable', 'healthy', true, 'Not logged in', null, 'signed-out'),
      NOW,
    )[0];

    expect(signedOut.state).toBe('signed-out');
    expect(signedOut.stateLabel).toBe('genuinely signed out, re-auth needed');
  });

  it('does not block on a credential timestamp while the fresh probe remains ready', () => {
    const badge = providerAuthBadgesForSnapshot(
      snapshot(
        'ready',
        'healthy',
        true,
        'credentials expiring',
        new Date(NOW - 60_000).toISOString(),
        'credentials-expiring',
      ),
      NOW,
    )[0];

    expect(badge.state).toBe('expiring');
    expect(badge.detail).toContain('latest provider probe remains ready');
  });

  it('holds a Ready card on its configured host until usable auth is advertised', () => {
    const task = {
      state: '2-ready',
      cliType: 'claude',
      executionLocation: {
        state: 'queued-remote',
        executionKind: 'remote',
        runnerId: 'agent-runner-01',
        configuredRunnerId: 'agent-runner-01',
        connectionState: 'queued',
        leaseState: 'none',
        trustReason: 'fixture',
      },
    } as TaskInfo;
    const unavailable = providerAuthBadgesForSnapshot(
      snapshot('unavailable', 'healthy', true, 'Not logged in'),
      NOW,
    );

    expect(providerAuthWaitReason(task, unavailable)).toMatchObject({
      label: 'Waiting for Claude sign-in on runner-berlin',
      hostNames: ['runner-berlin'],
    });
    expect(providerAuthWaitReason(task, providerAuthBadgesForSnapshot(snapshot('ready', 'healthy', true), NOW)))
      .toBeNull();
    expect(providerAuthWaitReason(task, providerAuthBadgesForSnapshot(
      snapshot('ready', 'suspect', true, 'retrying', null, 'transient-auth-error'),
      NOW,
    ))).toBeNull();
    expect(providerAuthWaitReason({ ...task, state: '3-progress' }, unavailable)).toBeNull();
  });

  it('holds an unassigned Ready card when no reachable runner has usable auth', () => {
    const task = {
      state: '2-ready',
      cliType: 'claude',
    } as TaskInfo;
    const unavailable = providerAuthBadgesForSnapshot(
      snapshot('unavailable', 'healthy', true, 'Not logged in'),
      NOW,
    );

    expect(providerAuthWaitReason(task, unavailable)).toMatchObject({
      label: 'Waiting for Claude sign-in on runner-berlin',
    });
    expect(providerAuthWaitReason(task, providerAuthBadgesForSnapshot(
      snapshot('ready', 'healthy', true),
      NOW,
    ))).toBeNull();
  });
});

function snapshot(
  advertisedStatus: string,
  healthState: 'healthy' | 'suspect' | 'draining' | 'half-open',
  isFresh: boolean,
  detail = 'Active session confirmed',
  expiresAt: string | null = null,
  condition: 'ok' | 'transient-auth-error' | 'credentials-expiring' | 'signed-out' | null = null,
): TaskServerRunnerCapabilitySnapshot {
  return {
    runnerId: 'agent-runner-01',
    name: 'runner-berlin',
    hostId: 'host-berlin',
    instanceId: 'coding-1',
    runnerVersion: '1.2.0',
    protocolVersion: 2,
    status: 'active',
    registeredAt: '2026-08-01T12:00:00Z',
    lastSeenAt: '2026-08-04T11:59:50Z',
    hostAdmission: { hostId: 'host-berlin', admissionState: 'open' },
    capabilities: [{
      key: 'cli-execution:claude',
      category: 'cli-execution',
      advertisedStatus: 'ready',
      healthState: 'healthy',
      advertisedAt: '2026-08-04T11:59:30Z',
      freshUntil: '2026-08-04T12:02:30Z',
      isFresh: true,
      consecutiveFailures: 0,
      affectedClaims: [],
      recoveryHistory: [],
    }, {
      key: 'provider-auth:claude',
      category: 'provider-auth',
      advertisedStatus,
      healthState,
      advertisedAt: '2026-08-04T11:59:30Z',
      freshUntil: isFresh ? '2026-08-04T12:02:30Z' : '2026-08-04T11:58:00Z',
      isFresh,
      consecutiveFailures: healthState === 'healthy' ? 0 : 1,
      detail,
      condition,
      expiresAt,
      affectedClaims: [],
      recoveryHistory: [],
    }],
  };
}
