import { Injectable, signal } from '@angular/core';
import type { HostActionKind, RemoteHost } from '../models/remote-host.model';
import { seedRemoteHosts } from './remote-hosts.seed';

/**
 * Registry + action service for the Remote-Hosts page (AGT-1921).
 *
 * UI-first: the host list is served from a static configuration seed
 * ({@link seedRemoteHosts}) that mirrors the shape a heartbeat-fed
 * `GET /api/hosts` endpoint will later return. When that endpoint lands, only
 * {@link reload} changes - it swaps the seed for an HTTP call and the component
 * keeps reading `hosts()` unchanged.
 *
 * The Re-Probe / Drain / Retire actions are applied optimistically against the
 * in-memory registry (there is no backend command surface yet) and emit
 * structured `remote-host.*` console events so the behaviour is observable in
 * the browser log ahead of real wiring.
 */
@Injectable({ providedIn: 'root' })
export class RemoteHostsService {
  /** The registry, newest snapshot wins. */
  readonly hosts = signal<RemoteHost[]>([]);
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);

  private loaded = false;

  /** Simulated latency for the optimistic actions (ms). */
  private static readonly ACTION_DELAY_MS = 550;

  /** Load once on first mount; explicit reloads re-seed the registry. */
  ensureLoaded(): void {
    if (this.loaded) return;
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    try {
      // Static registry for now (UI-first). This is the single line that
      // becomes an HTTP fetch once the heartbeat-fed endpoint exists.
      this.hosts.set(seedRemoteHosts(Date.now()));
      this.loaded = true;
      this.log('loaded', { count: this.hosts().length });
    } catch (e) {
      this.error.set('Failed to load the host registry.');
      this.log('load-failed', { message: (e as Error)?.message ?? 'unknown' });
    } finally {
      this.loading.set(false);
    }
  }

  /** Re-run the runner probes for a host: refreshes its heartbeat + load. */
  reprobe(id: string): void {
    this.runAction(id, 'reprobe', (host) => ({
      ...host,
      lastHeartbeatAt: new Date().toISOString(),
      status: host.status === 'offline' || host.status === 'degraded' ? 'online' : host.status,
      stats: host.stats
        ? { ...host.stats, cpuLoadPct: jitterLoad(host.stats.cpuLoadPct) }
        : host.stats,
    }));
  }

  /** Ask a host to finish current work and stop taking more. */
  drain(id: string): void {
    this.runAction(id, 'drain', (host) => ({ ...host, status: 'draining' }));
  }

  /** Permanently remove a host from the pool. Retired hosts report no stats. */
  retire(id: string): void {
    this.runAction(id, 'retire', (host) => ({ ...host, status: 'retired', stats: null }));
  }

  /**
   * Apply an action optimistically: flag the row busy, then commit the change
   * after a short simulated delay. Concurrent actions on the same host are
   * ignored while one is in flight.
   */
  private runAction(id: string, kind: HostActionKind, apply: (host: RemoteHost) => RemoteHost): void {
    const current = this.hosts().find((h) => h.id === id);
    if (!current || current.busyAction) return;

    this.log('action', { kind, hostId: id });
    this.patch(id, (host) => ({ ...host, busyAction: kind }));

    setTimeout(() => {
      this.patch(id, (host) => ({ ...apply(host), busyAction: null }));
      this.log('action-applied', { kind, hostId: id });
    }, RemoteHostsService.ACTION_DELAY_MS);
  }

  private patch(id: string, apply: (host: RemoteHost) => RemoteHost): void {
    this.hosts.update((list) => list.map((h) => (h.id === id ? apply(h) : h)));
  }

  private log(event: string, detail: Record<string, unknown>): void {
    // Stable event names so the browser log reads as a domain feed while the
    // real backend command surface is still being built.
    console.info(`[remote-hosts] ${event}`, { event: `remote-host.${event}`, ...detail });
  }
}

/** Nudge a CPU-load reading within a plausible band on re-probe. */
function jitterLoad(load: number): number {
  const delta = (load % 7) - 3; // deterministic-ish nudge, no Math.random dependency
  return Math.max(2, Math.min(96, load + delta));
}
