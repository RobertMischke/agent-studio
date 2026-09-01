import { provideHttpClient } from '@angular/common/http';
import { provideHttpClientTesting } from '@angular/common/http/testing';
import { provideZonelessChangeDetection } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { beforeEach, describe, expect, it } from 'vitest';
import { NotificationService } from '../../../services/notification.service';
import type { TaskServerRunnerCapabilitySnapshot } from '../models/remote-host.model';
import { ProviderAuthStatusService } from './provider-auth-status.service';

describe('ProviderAuthStatusService', () => {
  let service: ProviderAuthStatusService;
  let notifications: NotificationService;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideZonelessChangeDetection(),
        provideHttpClient(),
        provideHttpClientTesting(),
      ],
    });
    service = TestBed.inject(ProviderAuthStatusService);
    notifications = TestBed.inject(NotificationService);
  });

  it('notifies only when a previously OK provider becomes unavailable', () => {
    service.ingest([snapshot('ready')]);
    expect(notifications.notifications()).toHaveLength(0);

    service.ingest([snapshot('unavailable', null, 'signed-out')]);

    expect(notifications.notifications()).toHaveLength(1);
    expect(notifications.notifications()[0].title).toBe('Claude sign-in required');
    expect(notifications.notifications()[0].message).toContain('genuinely signed out');
    expect(notifications.notifications()[0].message).toContain('runner-berlin');
  });

  it('warns once when a known expiry enters the final fourteen days', () => {
    const expiresAt = new Date(Date.now() + 10 * 24 * 60 * 60_000).toISOString();

    service.ingest([snapshot('ready', expiresAt)]);
    service.ingest([snapshot('ready', expiresAt)]);

    expect(notifications.notifications()).toHaveLength(1);
    expect(notifications.notifications()[0].title).toBe('Claude credentials expiring');
    expect(notifications.notifications()[0].message).toContain('expires in 10 days');
    expect(notifications.notifications()[0].kind).toBe('info');
  });

  it('reports retrying and rate-limited as quiet states rather than sign-in alarms', () => {
    service.ingest([snapshot('ready')]);
    service.ingest([snapshot('ready', null, 'transient-error')]);
    service.ingest([snapshot('unavailable', null, 'rate-limited')]);

    expect(notifications.notifications().map(item => item.title)).toEqual([
      'Claude auth retrying',
      'Claude rate-limited',
    ]);
    expect(notifications.notifications().every(item => item.kind === 'info')).toBe(true);
  });
});

function snapshot(
  status: 'ready' | 'unavailable',
  expiresAt: string | null = null,
  condition: 'authenticated' | 'transient-error' | 'rate-limited' | 'expiring' | 'signed-out' | null = null,
): TaskServerRunnerCapabilitySnapshot {
  const now = new Date().toISOString();
  return {
    runnerId: 'agent-runner-01',
    name: 'runner-berlin',
    hostId: 'host-berlin',
    instanceId: 'coding-1',
    runnerVersion: '1.2.0',
    protocolVersion: 2,
    status: 'active',
    registeredAt: now,
    lastSeenAt: now,
    hostAdmission: { hostId: 'host-berlin', admissionState: 'open' },
    capabilities: [{
      key: 'cli-execution:claude', category: 'cli-execution', advertisedStatus: 'ready',
      healthState: 'healthy', advertisedAt: now,
      freshUntil: new Date(Date.now() + 120_000).toISOString(), isFresh: true,
      consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [],
    }, {
      key: 'provider-auth:claude', category: 'provider-auth', advertisedStatus: status,
      healthState: 'healthy', advertisedAt: now,
      freshUntil: new Date(Date.now() + 120_000).toISOString(), isFresh: true,
      consecutiveFailures: 0, detail: status === 'ready' ? 'Active session confirmed' : 'Not logged in',
      condition, expiresAt, affectedClaims: [], recoveryHistory: [],
    }],
  };
}
