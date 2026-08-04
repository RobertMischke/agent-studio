import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { afterEach, beforeEach, describe, expect, it } from 'vitest';
import { NotificationService } from '../../../services/notification.service';
import type { RemoteHost } from '../models/remote-host.model';
import { ProviderAuthNotificationBridge } from './provider-auth-notification-bridge.service';
import { RemoteHostsService } from './remote-hosts.service';

const HANDLED_KEY = 'atp.providerAuth.handled';

describe('ProviderAuthNotificationBridge', () => {
  beforeEach(() => localStorage.removeItem(HANDLED_KEY));
  afterEach(() => {
    TestBed.resetTestingModule();
    localStorage.removeItem(HANDLED_KEY);
  });

  it('notifies once when a provider changes from ready to unavailable', () => {
    const hosts = signal<RemoteHost[]>([host('ready')]);
    TestBed.configureTestingModule({
      providers: [
        NotificationService,
        ProviderAuthNotificationBridge,
        { provide: RemoteHostsService, useValue: { hosts } },
      ],
    });
    TestBed.inject(ProviderAuthNotificationBridge);
    const notifications = TestBed.inject(NotificationService);
    TestBed.tick();

    hosts.set([host('unavailable')]);
    TestBed.tick();
    hosts.set([host('unavailable')]);
    TestBed.tick();

    expect(notifications.notifications()).toHaveLength(1);
    expect(notifications.notifications()[0]).toMatchObject({
      kind: 'error',
      title: 'Claude Code sign-in required',
    });
    expect(notifications.notifications()[0].message).toContain('agent-runner-01');
  });

  it('warns when known credential expiry enters the final 14 days', () => {
    const hosts = signal<RemoteHost[]>([
      host('ready', new Date(Date.now() + 10 * 24 * 60 * 60_000).toISOString()),
    ]);
    TestBed.configureTestingModule({
      providers: [
        NotificationService,
        ProviderAuthNotificationBridge,
        { provide: RemoteHostsService, useValue: { hosts } },
      ],
    });
    TestBed.inject(ProviderAuthNotificationBridge);
    TestBed.tick();

    expect(TestBed.inject(NotificationService).notifications()[0]).toMatchObject({
      kind: 'warning',
      title: 'Claude Code credential expires soon',
    });
  });
});

function host(status: string, expiresAt: string | null = null): RemoteHost {
  const timestamp = new Date().toISOString();
  return {
    id: 'runner-a', clientId: 'runner-a', name: 'agent-runner-01', role: 'remote',
    address: null, status: 'online', os: 'Linux', lastHeartbeatAt: timestamp,
    uptimeLabel: null, capabilities: [], cliQuotas: [], stats: null,
    capabilityHealth: [
      {
        key: 'cli-execution:claude', category: 'cli-execution', advertisedStatus: 'ready',
        healthState: 'healthy', advertisedAt: timestamp, freshUntil: timestamp,
        isFresh: true, consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [],
      },
      {
        key: 'provider-auth:claude', category: 'provider-auth', advertisedStatus: status,
        healthState: 'healthy', detail: status === 'ready' ? 'active session' : 'Not logged in',
        advertisedAt: timestamp, freshUntil: timestamp, isFresh: true,
        consecutiveFailures: 0, affectedClaims: [], recoveryHistory: [], expiresAt,
      },
    ],
  };
}
