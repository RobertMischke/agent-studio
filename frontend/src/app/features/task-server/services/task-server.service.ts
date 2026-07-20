import { HttpClient, HttpHeaders } from '@angular/common/http';
import { Injectable, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import type {
  ManagementActionKind,
  ManagementActionResult,
  TaskServerStatus,
} from '../models/task-server.model';
import { isLocalUrl } from '../models/task-server.model';

interface ApiStatus {
  server: { id: string; url: string; version: string; protocolMinimum: string; protocolMaximum: string; uptimeSeconds: number };
  health: { state: 'healthy' | 'degraded' | 'maintenance'; ready: boolean };
  store: { sizeBytes: number; projectCount: number; taskCount: number; archivedTaskCount: number; eventCount: number; artifactCount: number; identityCount: number };
  evidence: { state: string; eventFiles: number; artifactFiles: number; lastWriteAt: string | null };
  maintenance: TaskServerStatus['maintenance'];
  migrations: TaskServerStatus['migrations'];
  runners: readonly {
    id: string; displayName: string; state: string; lastUsedAt: string | null;
    activeSlots: number; drainRequested: boolean; retireRequested: boolean;
  }[];
  backups: TaskServerStatus['backups'];
  security: TaskServerStatus['security'];
}

interface ApiCommandResult {
  commandId: string; kind: ManagementActionKind; dryRun: boolean; state: string;
  matched: number; affected: number; summary: string; completedAt: string;
}

@Injectable({ providedIn: 'root' })
export class TaskServerService {
  private readonly http = inject(HttpClient);
  readonly status = signal<TaskServerStatus | null>(null);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly busyAction = signal<ManagementActionKind | null>(null);
  readonly recentResults = computed<readonly ManagementActionResult[]>(() => this.status()?.recentResults ?? []);
  private loaded = false;

  ensureLoaded(): void { if (!this.loaded) void this.reload(); }

  async reload(): Promise<void> {
    this.loading.set(true);
    this.error.set(null);
    try {
      const api = await firstValueFrom(this.http.get<ApiStatus>('/api/v1/management/status'));
      const prior = this.status()?.recentResults ?? [];
      this.status.set(this.mapStatus(api, prior));
      this.loaded = true;
    } catch {
      this.error.set('The authenticated Task Server management API is unavailable.');
    } finally {
      this.loading.set(false);
    }
  }

  async runAction(kind: ManagementActionKind, confirmed = false): Promise<void> {
    if (this.busyAction()) return;
    this.busyAction.set(kind);
    this.error.set(null);
    try {
      const idempotencyKey = crypto.randomUUID();
      const body = {
        kind,
        dryRun: !confirmed,
        confirmation: confirmed ? kind : null,
        idempotencyKey,
      };
      const result = await firstValueFrom(this.http.post<ApiCommandResult>(
        '/api/v1/management/commands', body,
        { headers: new HttpHeaders({ 'Idempotency-Key': idempotencyKey }) },
      ));
      const snapshot = this.status();
      if (snapshot) {
        const mapped: ManagementActionResult = {
          kind: result.kind,
          ranAt: result.completedAt,
          summary: result.summary,
          affected: result.affected,
          matched: result.matched,
          dryRun: result.dryRun,
          commandId: result.commandId,
          state: result.state,
        };
        this.status.set({ ...snapshot, recentResults: [mapped, ...snapshot.recentResults].slice(0, 12) });
      }
      if (confirmed) await this.reload();
    } catch {
      this.error.set(`The ${kind} command failed. No server files were changed by the console.`);
    } finally {
      this.busyAction.set(null);
    }
  }

  private mapStatus(api: ApiStatus, recentResults: readonly ManagementActionResult[]): TaskServerStatus {
    const health = api.health.state === 'maintenance' ? 'degraded' : api.health.state;
    return {
      connection: {
        id: api.server.id,
        url: api.server.url,
        phase: isLocalUrl(api.server.url) ? 'local' : 'central',
        health,
        version: api.server.version,
        uptimeLabel: formatUptime(api.server.uptimeSeconds),
        protocolMinimum: api.server.protocolMinimum,
        protocolMaximum: api.server.protocolMaximum,
        ready: api.health.ready,
        authMode: 'Authenticated session / scoped Runner credential',
      },
      store: { ...api.store, root: 'Server-owned data directory (path is not exposed)' },
      evidence: {
        branch: 'server evidence store',
        state: api.evidence.state === 'available' ? 'clean' : 'dirty',
        uncommittedFiles: 0,
        ahead: 0,
        behind: 0,
        lastCommitSha: null,
        lastCommitSubject: `${api.evidence.eventFiles} event files · ${api.evidence.artifactFiles} artifact files`,
        lastCommitAt: api.evidence.lastWriteAt,
      },
      clients: api.runners.map(runner => ({
        id: runner.id,
        displayName: runner.displayName,
        emoji: null,
        kind: runner.state === 'retired' ? 'retired' : 'agent-instance',
        lastSeenAt: runner.lastUsedAt,
        ownedTaskCount: runner.activeSlots,
        managementState: runner.state,
      })),
      maintenance: api.maintenance,
      migrations: api.migrations,
      backups: api.backups,
      security: api.security,
      recentResults,
    };
  }
}

function formatUptime(totalSeconds: number): string {
  const days = Math.floor(totalSeconds / 86_400);
  const hours = Math.floor((totalSeconds % 86_400) / 3_600);
  const minutes = Math.floor((totalSeconds % 3_600) / 60);
  return days ? `${days}d ${hours}h` : hours ? `${hours}h ${minutes}m` : `${minutes}m`;
}
