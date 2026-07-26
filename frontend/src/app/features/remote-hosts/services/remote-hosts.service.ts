import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import type { ClientSummary } from '../../../models/task.model';
import type {
  HostActionKind,
  HostTelemetrySeries,
  RemoteHost,
  RemoteHostAdmission,
  RemoteHostCapabilityHealth,
} from '../models/remote-host.model';
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

  /**
   * Refresh the compact live view without replacing a longer telemetry series
   * already loaded by the Execution Hosts page.
   */
  refresh(): void {
    if (this.hosts().length === 0) {
      this.hosts.set(seedRemoteHosts(Date.now()).map(host => ({
        ...host,
        liveDataState: 'loading',
        telemetryLoading: false,
      })));
    }
    this.hydrateClientRegistry('1h', true);
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

  private hydrateClientRegistry(
    telemetryWindow: '1h' | '14d' = '14d',
    preserveLongTelemetry = false,
  ): void {
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
          const projectedHost = projectClient(host, client, client.drainRequestedAt ? 'draining' : status);
          return preserveLongTelemetry && host.telemetry?.window === '14d'
            ? { ...projectedHost, stats: host.stats, telemetry: host.telemetry }
            : projectedHost;
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
          this.patch(host.id, current => preserveLongTelemetry && current.telemetry?.window === '14d'
            ? { ...current, telemetryLoading: true }
            : { ...current, stats: null, telemetry: null, telemetryLoading: true });
          this.hydrateTelemetry(host.id, host.clientId, telemetryWindow, preserveLongTelemetry);
        }
        this.hydrateCapabilityRegistry();
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

  private hydrateCapabilityRegistry(): void {
    if (!this.http) return;
    this.http.get<TaskServerRunnerCapabilitySnapshot[]>('/api/v1/management/remote-hosts').subscribe({
      next: snapshots => {
        const now = Date.now();
        this.hosts.update(hosts => {
          const projected = [...hosts];
          for (const snapshot of snapshots ?? []) {
            const index = projected.findIndex(host =>
              host.clientId === snapshot.runnerId || host.id === snapshot.runnerId);
            const current = index >= 0 ? projected[index] : {
              id: snapshot.runnerId,
              name: snapshot.name,
              role: 'remote' as const,
              address: null,
              clientId: snapshot.runnerId,
              status: 'offline' as const,
              os: 'Remote runner',
              lastHeartbeatAt: null,
              uptimeLabel: null,
              capabilities: [],
              cliQuotas: [],
              stats: null,
            };
            const lastSeenMs = Date.parse(snapshot.lastSeenAt);
            const heartbeatFresh = Number.isFinite(lastSeenMs)
              && now - lastSeenMs <= RemoteHostsService.DEGRADED_CLIENT_MS;
            const capabilityDegraded = snapshot.capabilities.some(capability =>
              !capability.isFresh || capability.healthState !== 'healthy' || capability.advertisedStatus !== 'ready');
            const hostDraining = snapshot.hostAdmission.admissionState !== 'open';
            const telemetryAt = snapshot.telemetry ? Date.parse(snapshot.telemetry.observedAt) : Number.NaN;
            const telemetryFresh = snapshot.telemetry && Number.isFinite(telemetryAt)
              && now - telemetryAt <= RemoteHostsService.DEGRADED_CLIENT_MS
              && heartbeatFresh;
            const status = hostDraining
              ? 'draining'
              : !heartbeatFresh
                ? current.status
                : capabilityDegraded ? 'degraded' : current.status === 'offline' ? 'online' : current.status;
            const stats = telemetryFresh && snapshot.telemetry
              ? telemetryStats(snapshot.telemetry)
              : status === 'offline' ? null : current.stats;
            const gitPush = snapshot.capabilities.find(capability => capability.key === 'git:push');
            const gitWorkflowPush = snapshot.capabilities.find(
              capability => capability.key === 'git:workflow-push',
            );
            const gitPushStatus = gitPush && gitPush.advertisedStatus !== 'ready'
              ? 'read-only' as const
              : gitWorkflowPush?.advertisedStatus === 'ready-no-workflow-scope'
                ? 'ready-no-workflow-scope' as const
                : gitWorkflowPush?.advertisedStatus === 'ready' || gitPush?.advertisedStatus === 'ready'
                  ? 'ready' as const
                  : current.gitPushStatus;
            const next: RemoteHost = {
              ...current,
              name: snapshot.name,
              status,
              lastHeartbeatAt: snapshot.lastSeenAt,
              capabilityHealth: snapshot.capabilities,
              capabilities: snapshot.capabilities.map(capability =>
                capability.version ? `${capability.key} ${capability.version}` : capability.key),
              gitPushStatus,
              gitPushDetail: gitWorkflowPush?.detail ?? gitPush?.detail ?? current.gitPushDetail,
              gitPushCheckedAt:
                gitWorkflowPush?.advertisedAt ?? gitPush?.advertisedAt ?? current.gitPushCheckedAt,
              hostAdmission: snapshot.hostAdmission,
              stats,
            };
            if (index >= 0) projected[index] = next;
            else projected.push(next);
          }
          return projected;
        });
        this.log('capabilities-hydrated', { runners: snapshots?.length ?? 0 });
      },
      error: error => this.log('capabilities-hydrate-failed', { message: error?.message ?? 'unknown' }),
    });
  }

  private hydrateTelemetry(
    hostId: string,
    clientId: string,
    telemetryWindow: '1h' | '14d' = '14d',
    preserveLongTelemetry = false,
  ): void {
    if (!this.http) return;
    const startedAt = performance.now();
    this.http.get<HostTelemetrySeries>(
      `/api/clients/${encodeURIComponent(clientId)}/telemetry?window=${telemetryWindow}`,
    ).subscribe({
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
          const nextTelemetry = preserveLongTelemetry && host.telemetry?.window === '14d'
            ? mergeRecentTelemetry(host.telemetry, telemetry)
            : telemetry;
          return { ...host, stats, telemetry: nextTelemetry, telemetryLoading: false };
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

  reprobe(id: string): void {
    const host = this.hosts().find(item => item.id === id);
    if (!host || host.busyAction || !this.http) return;
    this.log('reprobe-requested', { hostId: id });
    this.patch(id, item => ({ ...item, busyAction: 'reprobe' }));
    this.http.post(`/api/clients/${encodeURIComponent(host.clientId)}/runner-project-preflights/invalidate`, {}).subscribe({
      next: () => this.reload(),
      error: error => this.actionFailed(id, error),
    });
  }

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

interface TaskServerTelemetrySnapshot {
  observedAt: string;
  cpuPercent: number | null;
  memoryUsedBytes: number | null;
  memoryTotalBytes: number | null;
  cpuCores: number;
  diskFreeBytes?: number | null;
  diskTotalBytes?: number | null;
}

interface TaskServerRunnerCapabilitySnapshot {
  runnerId: string;
  name: string;
  hostId: string;
  instanceId: string;
  runnerVersion: string;
  protocolVersion: number;
  status: string;
  registeredAt: string;
  lastSeenAt: string;
  hostAdmission: RemoteHostAdmission;
  capabilities: RemoteHostCapabilityHealth[];
  telemetry?: TaskServerTelemetrySnapshot | null;
}

function telemetryStats(telemetry: TaskServerTelemetrySnapshot): NonNullable<RemoteHost['stats']> {
  return {
    ramTotalMb: (telemetry.memoryTotalBytes ?? 0) / 1024 / 1024,
    ramFreeMb: Math.max(0, ((telemetry.memoryTotalBytes ?? 0) - (telemetry.memoryUsedBytes ?? 0)) / 1024 / 1024),
    cpuCores: telemetry.cpuCores,
    cpuModel: 'reported by daemon',
    cpuLoadPct: telemetry.cpuPercent ?? 0,
    diskTotalGb: (telemetry.diskTotalBytes ?? 0) / 1024 / 1024 / 1024,
    diskFreeGb: (telemetry.diskFreeBytes ?? 0) / 1024 / 1024 / 1024,
  };
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
    projectPreflights: client.runnerProjectPreflights ?? [],
    daemonState: status === 'offline' || status === 'retired' ? 'stopped' : client.runnerDaemonState ?? 'running',
    lastClaimAt: client.runnerLastClaimAt ?? null, activeTaskCount: client.runnerActiveSlots ?? 0,
    availableSlots: client.runnerAvailableSlots ?? 0, retireRequestedAt: client.retireRequestedAt ?? null,
    activeGateCount: client.runnerActiveGateCount ?? 0, gateCapacity: client.runnerGateCapacity ?? 0,
  };
}

function isRunnerIdentity(client: ClientSummary): boolean {
  return client.kind === 'retired' || !!client.runnerGitStatus || /runner|host/i.test(`${client.id} ${client.displayName}`);
}

function mergeRecentTelemetry(
  existing: HostTelemetrySeries,
  recent: HostTelemetrySeries,
): HostTelemetrySeries {
  const points = new Map(
    [...existing.points, ...recent.points].map(point => [point.timestamp, point]),
  );
  const findings = new Map(
    [...existing.findings, ...recent.findings]
      .map(finding => [
        `${finding.kind}:${finding.isActive === false ? 'history' : 'active'}`,
        finding,
      ]),
  );
  return {
    ...existing,
    points: [...points.values()].sort((a, b) => a.timestamp.localeCompare(b.timestamp)),
    findings: [...findings.values()],
  };
}

function tryInjectHttpClient(): HttpClient | null {
  try {
    return inject(HttpClient, { optional: true });
  } catch {
    return null;
  }
}
