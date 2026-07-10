import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
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
    expect(svc.loading()).toBe(false);
    expect(svc.error()).toBeNull();
  });

  it('ensureLoaded only seeds once; reload re-seeds explicitly', () => {
    const spy = vi.spyOn(svc, 'reload');
    svc.ensureLoaded();
    svc.ensureLoaded();
    expect(spy).toHaveBeenCalledTimes(1);
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
