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
 * Lifecycle actions call the persisted client API. No seeded value is treated
 * as live telemetry.
 */
@Injectable({ providedIn: 'root' })
export class RemoteHostsService {
  /** The registry, newest snapshot wins. */
  readonly hosts = signal<RemoteHost[]>([]);
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);

  private static readonly FRESH_CLIENT_MS = 90_000;
  private static readonly DEGRADED_CLIENT_MS = 5 * 60_000;
  /** Optional keeps direct-constructor pure tests and non-HTTP previews viable. */
  private readonly http = tryInjectHttpClient();

  /** Every mount revalidates live state; cached cards are never authoritative. */
  ensureLoaded(): void {
    this.reload();
  }

  /** Refresh live client and telemetry data without replacing the visible registry seed. */
  refresh(): void {
    if (!this.loaded) {
      this.reload();
      return;
    }
    this.hydrateClientRegistry();
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    try {
      const current = this.hosts();
      this.hosts.set((current.length ? current : seedRemoteHosts(Date.now())).map(host => ({
        ...host,
        liveDataState: 'loading',
        telemetryLoading: false,
      })));
      this.log('loaded', { count: this.hosts().length });
      this.hydrateClientRegistry();
    } catch (e) {
      this.error.set('Failed to load the host registry.');
      this.log('load-failed', { message: (e as Error)?.message ?? 'unknown' });
      this.loading.set(false);
    }
  }

  private hydrateClientRegistry(): void {
    if (!this.http) {
      this.hosts.update(hosts => hosts.map(host => ({ ...host, liveDataState: 'error' })));
      this.loading.set(false);
      return;
    }
    const startedAt = performance.now();
    this.http.get<ClientSummary[]>('/api/clients').subscribe({
      next: clients => {
        const byId = new Map((clients ?? []).map(client => [client.id, client]));
        const now = Date.now();
        this.hosts.update(hosts => {
          const projected = hosts.map(host => {
          const client = byId.get(host.clientId);
          if (!client) return {
            ...host,
            status: 'offline' as const,
            stats: null,
            liveDataState: 'ready' as const,
            telemetryLoading: false,
          };
          if (client.kind === 'retired') {
            return projectClient(host, client, 'retired');
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
          return projectClient(host, client, client.drainRequestedAt ? 'draining' : status);
          });
          const known = new Set(projected.map(host => host.clientId));
          const discovered = (clients ?? [])
            .filter(client => !known.has(client.id) && isRunnerIdentity(client))
            .map(client => projectClient({
              id: client.id, name: client.displayName, role: 'remote', address: null, clientId: client.id,
              status: 'offline', os: 'Remote runner', lastHeartbeatAt: null, uptimeLabel: null,
              capabilities: [], cliQuotas: [], stats: null, liveDataState: 'ready',
            }, client, client.kind === 'retired' ? 'retired' : client.drainRequestedAt ? 'draining' : statusFor(client.lastSeenAt, now)));
          return [...projected, ...discovered];
        });
        this.loading.set(false);
        this.log('clients-hydrated', {
          clients: clients?.length ?? 0,
          durationMs: Math.round(performance.now() - startedAt),
        });
        for (const host of this.hosts().filter(host => byId.has(host.clientId) && host.status !== 'retired')) {
          this.patch(host.id, current => ({
            ...current,
            stats: null,
            telemetry: null,
            telemetryLoading: true,
          }));
          this.hydrateTelemetry(host.id, host.clientId);
        }
      },
      error: error => {
        this.hosts.update(hosts => hosts.map(host => ({ ...host, liveDataState: 'error' })));
        this.loading.set(false);
        this.error.set('Live host status is temporarily unavailable.');
        this.log('clients-hydrate-failed', {
          message: error?.message ?? 'unknown',
          durationMs: Math.round(performance.now() - startedAt),
        });
      },
    });
  }

  private hydrateTelemetry(hostId: string, clientId: string): void {
    if (!this.http) return;
    const startedAt = performance.now();
    this.http.get<HostTelemetrySeries>(`/api/clients/${encodeURIComponent(clientId)}/telemetry?window=14d`).subscribe({
      next: telemetry => {
        this.patch(hostId, host => {
          const latest = telemetry.points.at(-1);
          const fresh = latest && Date.now() - Date.parse(latest.timestamp) <= RemoteHostsService.DEGRADED_CLIENT_MS
            && host.status !== 'offline' && host.status !== 'retired';
          const stats = fresh
            ? {
                cpuCores: latest.cpuCores || 0,
                cpuModel: 'reported by daemon',
                cpuLoadPct: latest.cpuPercent ?? 0,
                ramTotalMb: latest.memoryTotalBytes ? latest.memoryTotalBytes / 1024 / 1024 : 0,
                ramFreeMb: latest.memoryTotalBytes && latest.memoryUsedBytes !== null
                  ? (latest.memoryTotalBytes - latest.memoryUsedBytes) / 1024 / 1024
                  : 0,
                diskTotalGb: 0,
                diskFreeGb: 0,
              }
            : null;
          return { ...host, stats, telemetry, telemetryLoading: false };
        });
        this.log('telemetry-hydrated', { hostId, points: telemetry.points.length, findings: telemetry.findings.length,
          durationMs: Math.round(performance.now() - startedAt) });
      },
      error: error => {
        this.patch(hostId, host => ({ ...host, stats: null, telemetry: null, telemetryLoading: false }));
        this.log('telemetry-hydrate-failed', { hostId, message: error?.message ?? 'unknown',
          durationMs: Math.round(performance.now() - startedAt) });
      },
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
      liveDataState: 'ready',
    };
    this.hosts.update((hosts) => [...hosts.filter((item) => item.id !== id), host]);
    this.log('wizard-completed', { hostId: id, address: host.address });
  }

  reprobe(id: string): void { this.log('reprobe-requested', { hostId: id }); this.reload(); }

  /** Ask a host to finish current work and stop taking more. */
  drain(id: string): void {
    this.postAction(id, 'drain', `/api/clients/${encodeURIComponent(this.clientId(id))}/drain`);
  }

  /** Drain immediately and retire only after the daemon reports zero active slots. */
  retire(id: string): void {
    this.postAction(id, 'retire', `/api/clients/${encodeURIComponent(this.clientId(id))}/retire`);
  }

  revive(id: string): void {
    this.postAction(id, 'revive', `/api/clients/${encodeURIComponent(this.clientId(id))}/revive`);
  }

  permanentlyDelete(id: string): void {
    const host = this.hosts().find(item => item.id === id);
    if (!host || !this.http) return;
    this.patch(id, item => ({ ...item, busyAction: 'delete' }));
    this.http.delete(`/api/clients/${encodeURIComponent(host.clientId)}/permanent`).subscribe({
      next: () => this.hosts.update(items => items.filter(item => item.id !== id)),
      error: error => this.actionFailed(id, error),
    });
  }

  /**
   * Apply an action optimistically: flag the row busy, then commit the change
   * after a short simulated delay. Concurrent actions on the same host are
   * ignored while one is in flight.
   */
  private postAction(id: string, kind: HostActionKind, url: string): void {
    const current = this.hosts().find((h) => h.id === id);
    if (!current || current.busyAction || !this.http) return;

    this.log('action', { kind, hostId: id });
    this.patch(id, (host) => ({ ...host, busyAction: kind }));

    this.http.post<ClientSummary>(url, {}).subscribe({
      next: () => { this.log('action-applied', { kind, hostId: id }); this.reload(); },
      error: error => this.actionFailed(id, error),
    });
  }

  private actionFailed(id: string, error: unknown): void {
    this.patch(id, host => ({ ...host, busyAction: null }));
    this.error.set('The host lifecycle change could not be saved. Nothing was changed.');
    this.log('action-failed', { hostId: id, message: (error as { message?: string })?.message ?? 'unknown' });
  }

  private clientId(id: string): string { return this.hosts().find(host => host.id === id)?.clientId ?? id; }

  private patch(id: string, apply: (host: RemoteHost) => RemoteHost): void {
    this.hosts.update((list) => list.map((h) => (h.id === id ? apply(h) : h)));
  }

  private log(event: string, detail: Record<string, unknown>): void {
    // Stable event names so the browser log reads as a domain feed while the
    // real backend command surface is still being built.
    console.info(`[remote-hosts] ${event}`, { event: `remote-host.${event}`, ...detail });
  }
}

function statusFor(lastSeenAt: string | null, now: number): RemoteHost['status'] {
  const seen = lastSeenAt ? Date.parse(lastSeenAt) : Number.NaN;
  if (Number.isNaN(seen) || now - seen > 5 * 60_000) return 'offline';
  return now - seen <= 90_000 ? 'online' : 'degraded';
}

function projectClient(host: RemoteHost, client: ClientSummary, status: RemoteHost['status']): RemoteHost {
  return {
    ...host, status, lastHeartbeatAt: client.lastSeenAt, stats: null, telemetry: null,
    liveDataState: 'ready', telemetryLoading: status !== 'retired',
    gitPushStatus: client.runnerGitStatus ?? null, gitPushDetail: client.runnerGitDetail ?? null,
    gitPushCheckedAt: client.runnerGitCheckedAt ?? null,
    daemonState: status === 'offline' || status === 'retired' ? 'stopped' : client.runnerDaemonState ?? (client.runnerGitStatus === 'read-only' ? 'read-only' : 'running'),
    lastClaimAt: client.runnerLastClaimAt ?? null, activeTaskCount: client.runnerActiveSlots ?? 0,
    availableSlots: client.runnerAvailableSlots ?? 0, retireRequestedAt: client.retireRequestedAt ?? null,
    activeGateCount: client.runnerActiveGateCount ?? 0, gateCapacity: client.runnerGateCapacity ?? 0,
  };
}

function isRunnerIdentity(client: ClientSummary): boolean {
  return client.kind === 'retired' || !!client.runnerGitStatus || /runner|host/i.test(`${client.id} ${client.displayName}`);
}

function tryInjectHttpClient(): HttpClient | null {
  try {
    return inject(HttpClient, { optional: true });
  } catch {
    return null;
  }
}
