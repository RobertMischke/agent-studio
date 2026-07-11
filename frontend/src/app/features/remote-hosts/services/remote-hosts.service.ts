import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import type { ClientSummary } from '../../../models/task.model';
import type { HostActionKind, HostTelemetrySeries, RemoteHost } from '../models/remote-host.model';
import { seedRemoteHosts } from './remote-hosts.seed';

/**
 * Registry + action service for the Remote-Hosts page (AGT-1921).
 *
 * Host topology comes from the static configuration seed while liveness comes
 * from the real client registry. Reload hydrates each configured client id from
 * `GET /api/clients`, so runner polling updates LastSeen and status without a
 * second host-registry backend contract.
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
  private static readonly FRESH_CLIENT_MS = 90_000;
  private static readonly DEGRADED_CLIENT_MS = 5 * 60_000;
  /** Optional keeps direct-constructor pure tests and non-HTTP previews viable. */
  private readonly http = tryInjectHttpClient();

  /** Load once on first mount; explicit reloads re-seed the registry. */
  ensureLoaded(): void {
    if (this.loaded) return;
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    try {
      const retiredIds = new Set(this.hosts().filter(host => host.status === 'retired').map(host => host.id));
      this.hosts.set(seedRemoteHosts(Date.now()).map(host =>
        retiredIds.has(host.id) ? { ...host, status: 'retired', stats: null } : host));
      this.loaded = true;
      this.log('loaded', { count: this.hosts().length });
      this.hydrateClientRegistry();
    } catch (e) {
      this.error.set('Failed to load the host registry.');
      this.log('load-failed', { message: (e as Error)?.message ?? 'unknown' });
    } finally {
      this.loading.set(false);
    }
  }

  private hydrateClientRegistry(): void {
    if (!this.http) return;
    const startedAt = performance.now();
    this.http.get<ClientSummary[]>('/api/clients').subscribe({
      next: clients => {
        const byId = new Map((clients ?? []).map(client => [client.id, client]));
        const now = Date.now();
        this.hosts.update(hosts => hosts.map(host => {
          const client = byId.get(host.clientId);
          if (!client || host.status === 'retired') return host;
          if (client.kind === 'retired') {
            return { ...host, status: 'retired', lastHeartbeatAt: client.lastSeenAt, stats: null };
          }
          const seenAt = client.lastSeenAt;
          const seenMs = seenAt ? Date.parse(seenAt) : Number.NaN;
          const status = !seenAt || Number.isNaN(seenMs)
            ? 'offline'
            : now - seenMs <= RemoteHostsService.FRESH_CLIENT_MS
              ? 'online'
              : now - seenMs <= RemoteHostsService.DEGRADED_CLIENT_MS
                ? 'degraded'
                : 'offline';
          return {
            ...host,
            lastHeartbeatAt: seenAt,
            status,
            gitPushStatus: client.runnerGitStatus ?? null,
            gitPushDetail: client.runnerGitDetail ?? null,
          };
        }));
        this.log('clients-hydrated', {
          clients: clients?.length ?? 0,
          durationMs: Math.round(performance.now() - startedAt),
        });
        for (const host of this.hosts().filter(host => byId.has(host.clientId) && host.status !== 'retired')) {
          this.hydrateTelemetry(host.id, host.clientId);
        }
      },
      error: error => this.log('clients-hydrate-failed', {
        message: error?.message ?? 'unknown',
        durationMs: Math.round(performance.now() - startedAt),
      }),
    });
  }

  private hydrateTelemetry(hostId: string, clientId: string): void {
    if (!this.http) return;
    const startedAt = performance.now();
    this.http.get<HostTelemetrySeries>(`/api/clients/${encodeURIComponent(clientId)}/telemetry?window=14d`).subscribe({
      next: telemetry => {
        this.patch(hostId, host => {
          const latest = telemetry.points.at(-1);
          const stats = host.stats && latest
            ? {
                ...host.stats,
                cpuCores: latest.cpuCores || host.stats.cpuCores,
                cpuLoadPct: latest.cpuPercent ?? host.stats.cpuLoadPct,
                ramTotalMb: latest.memoryTotalBytes ? latest.memoryTotalBytes / 1024 / 1024 : host.stats.ramTotalMb,
                ramFreeMb: latest.memoryTotalBytes && latest.memoryUsedBytes !== null
                  ? (latest.memoryTotalBytes - latest.memoryUsedBytes) / 1024 / 1024
                  : host.stats.ramFreeMb,
              }
            : host.stats;
          return { ...host, stats, telemetry };
        });
        this.log('telemetry-hydrated', { hostId, points: telemetry.points.length, findings: telemetry.findings.length,
          durationMs: Math.round(performance.now() - startedAt) });
      },
      error: error => this.log('telemetry-hydrate-failed', { hostId, message: error?.message ?? 'unknown',
        durationMs: Math.round(performance.now() - startedAt) }),
    });
  }

  /** Add the host produced by the UI-first setup wizard to this registry. */
  addProvisionedHost(name: string, address: string): void {
    const id = name.trim().toLowerCase().replace(/[^a-z0-9-]+/g, '-') || `runner-${Date.now()}`;
    const host: RemoteHost = {
      id,
      name: name.trim() || id,
      role: 'remote',
      address: address.trim(),
      clientId: id,
      status: 'idle',
      os: 'Ubuntu LTS',
      lastHeartbeatAt: new Date().toISOString(),
      uptimeLabel: 'just provisioned',
      capabilities: ['linux', 'git', 'node 22', 'dotnet 10', 'playwright'],
      cliQuotas: [],
      stats: null,
    };
    this.hosts.update((hosts) => [...hosts.filter((item) => item.id !== id), host]);
    this.log('wizard-completed', { hostId: id, address: host.address });
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

function tryInjectHttpClient(): HttpClient | null {
  try {
    return inject(HttpClient, { optional: true });
  } catch {
    return null;
  }
}
