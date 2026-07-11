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

  it('ensureLoaded only seeds once; reload re-seeds explicitly', () => {
    const spy = vi.spyOn(svc, 'reload');
    svc.ensureLoaded();
    svc.ensureLoaded();
    expect(spy).toHaveBeenCalledTimes(1);
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

  it('drain flags the row busy, then settles it to draining', () => {
    svc.ensureLoaded();
    const id = svc.hosts()[0].id;

    svc.drain(id);
    expect(svc.hosts().find((h) => h.id === id)?.busyAction).toBe('drain');

    vi.runAllTimers();
    const host = svc.hosts().find((h) => h.id === id);
    expect(host?.busyAction).toBeNull();
    expect(host?.status).toBe('draining');
  });

  it('retire removes stats and marks the host retired', () => {
    svc.ensureLoaded();
    const id = svc.hosts()[0].id;

    svc.retire(id);
    vi.runAllTimers();

    const host = svc.hosts().find((h) => h.id === id);
    expect(host?.status).toBe('retired');
    expect(host?.stats).toBeNull();
  });

  it('reprobe refreshes the heartbeat timestamp', () => {
    svc.ensureLoaded();
    const id = svc.hosts()[0].id;
    const before = svc.hosts().find((h) => h.id === id)?.lastHeartbeatAt;

    vi.advanceTimersByTime(5_000);
    svc.reprobe(id);
    vi.runAllTimers();

    const after = svc.hosts().find((h) => h.id === id)?.lastHeartbeatAt;
    expect(after).not.toBe(before);
  });

  it('ignores a second action while one is already in flight for the host', () => {
    svc.ensureLoaded();
    const id = svc.hosts()[0].id;

    svc.drain(id);
    svc.retire(id); // must be ignored: drain is still busy
    vi.runAllTimers();

    expect(svc.hosts().find((h) => h.id === id)?.status).toBe('draining');
  });
});

describe('RemoteHostsService client registry hydration', () => {
  it('projects fresh and stale LastSeen while preserving retired hosts', () => {
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
    }]);
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')).toMatchObject({
      status: 'online',
      lastHeartbeatAt: new Date(now - 30_000).toISOString(),
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
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.status).toBe('degraded');

    svc.reload();
    http.expectOne('/api/clients').flush([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', emoji: null, colour: null,
      kind: 'agent-instance', registeredAt: new Date(now - 900_000).toISOString(),
      lastSeenAt: new Date(now - 600_000).toISOString(), tokenBudgetMonthly: null, notes: null,
    }]);
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.status).toBe('offline');

    svc.hosts.update(hosts => hosts.map(host =>
      host.id === 'agent-runner-01' ? { ...host, status: 'retired' } : host));
    svc.reload();
    http.expectOne('/api/clients').flush([{
      id: 'agent-runner-01', displayName: 'agent-runner-01', emoji: null, colour: null,
      kind: 'agent-instance', registeredAt: new Date(now).toISOString(),
      lastSeenAt: new Date(now).toISOString(), tokenBudgetMonthly: null, notes: null,
    }]);
    expect(svc.hosts().find(host => host.id === 'agent-runner-01')?.status).toBe('retired');
    http.verify();
  });
});
