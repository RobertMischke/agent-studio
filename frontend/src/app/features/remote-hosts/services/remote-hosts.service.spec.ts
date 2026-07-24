import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { RemoteHostsService } from './remote-hosts.service';

describe('RemoteHostsService', () => {
  let svc: RemoteHostsService;

  beforeEach(() => {
    vi.useFakeTimers();
    svc = new RemoteHostsService();
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.restoreAllMocks();
  });

  it('seeds the registry on first load with the local + remote hosts', () => {
    svc.ensureLoaded();
    const hosts = svc.hosts();
    expect(hosts.length).toBeGreaterThanOrEqual(2);
    expect(hosts.some((h) => h.role === 'local')).toBe(true);
    expect(hosts.some((h) => h.role === 'remote')).toBe(true);
    expect(hosts.find((h) => h.id === 'agent-runner-01')).toMatchObject({
      clientId: 'agent-runner-01',
      status: 'offline',
      lastHeartbeatAt: null,
      cliQuotas: [],
      stats: null,
    });
    expect(svc.loading()).toBe(false);
    expect(svc.error()).toBeNull();
  });

  it('ensureLoaded revalidates live state on every mount', () => {
    const spy = vi.spyOn(svc, 'reload');
    svc.ensureLoaded();
    svc.ensureLoaded();
    expect(spy).toHaveBeenCalledTimes(2);
  });

  it('adds a wizard-completed host as an idle remote runner', () => {
    svc.ensureLoaded();
    svc.addProvisionedHost('Runner Berlin 02', 'ssh://runner@berlin.example');

    const host = svc.hosts().find((item) => item.id === 'runner-berlin-02');
    expect(host).toMatchObject({
      name: 'Runner Berlin 02',
      address: 'ssh://runner@berlin.example',
      clientId: 'runner-berlin-02',
      role: 'remote',
      status: 'idle',
    });
  });

});

describe('RemoteHostsService client registry hydration', () => {
  it('projects fresh and stale LastSeen and takes retirement only from the server', () => {
    TestBed.configureTestingModule({
      providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()],
    });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    const now = Date.now();

    svc.reload();
    http.expectOne('/api/clients').flush([{
      id: 'agent-runner-01',
      displayName: 'agent-runner-01',
      emoji: null,
      colour: null,
      kind: 'agent-instance',
      registeredAt: new Date(now - 10_000).toISOString(),
      lastSeenAt: new Date(now - 30_000).toISOString(),
      tokenBudgetMonthly: null,
      notes: null,
      runnerGitStatus: 'read-only',
      runnerGitDetail: 'push-dry-run failed (128): permission denied',
      runnerActiveSlots: 1,
      runnerAvailableSlots: 19,
      runnerActiveGateCount: 2,
      runnerGateCapacity: 4,
    }]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({
      clientId: 'agent-runner-01', window: '14d', points: [], findings: [],
    });
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')).toMatchObject({
      status: 'online',
      lastHeartbeatAt: new Date(now - 30_000).toISOString(),
      gitPushStatus: 'read-only',
      gitPushDetail: 'push-dry-run failed (128): permission denied',
      activeTaskCount: 1,
      availableSlots: 19,
      activeGateCount: 2,
      gateCapacity: 4,
      liveDataState: 'ready',
    });

    svc.reload();
    http.expectOne('/api/clients').flush([{
      id: 'agent-runner-01',
      displayName: 'agent-runner-01',
      emoji: null,
      colour: null,
      kind: 'agent-instance',
      registeredAt: new Date(now - 300_000).toISOString(),
      lastSeenAt: new Date(now - 180_000).toISOString(),
      tokenBudgetMonthly: null,
      notes: null,
    }]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({
      clientId: 'agent-runner-01', window: '14d', points: [], findings: [],
    });
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.status).toBe('degraded');

    svc.reload();
    http.expectOne('/api/clients').flush([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', emoji: null, colour: null,
      kind: 'agent-instance', registeredAt: new Date(now - 900_000).toISOString(),
      lastSeenAt: new Date(now - 600_000).toISOString(), tokenBudgetMonthly: null, notes: null,
    }]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({
      clientId: 'agent-runner-01', window: '14d', points: [], findings: [],
    });
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.status).toBe('offline');

    svc.reload();
    http.expectOne('/api/clients').flush([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', emoji: null, colour: null,
      kind: 'retired', registeredAt: new Date(now).toISOString(),
      lastSeenAt: new Date(now).toISOString(), tokenBudgetMonthly: null, notes: null,
    }]);
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.status).toBe('retired');
    http.verify();
  });

  it('persists drain through the lifecycle API before reloading', () => {
    TestBed.configureTestingModule({ providers: [RemoteHostsService, provideHttpClient(), provideHttpClientTesting()] });
    const svc = TestBed.inject(RemoteHostsService);
    const http = TestBed.inject(HttpTestingController);
    svc.reload();
    http.expectOne('/api/clients').flush([]);

    svc.drain('agent-runner-01');
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.busyAction).toBe('drain');
    http.expectOne('/api/clients/agent-runner-01/drain').flush({ id: 'agent-runner-01' });
    http.expectOne('/api/clients').flush([{ id: 'agent-runner-01', displayName: 'agent-runner-01', kind: 'service', registeredAt: new Date().toISOString(), lastSeenAt: null, drainRequestedAt: new Date().toISOString() }]);
    http.expectOne('/api/clients/agent-runner-01/telemetry?window=14d').flush({ clientId: 'agent-runner-01', window: '14d', points: [], findings: [] });
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.status).toBe('draining');
    http.verify();
  });
});
