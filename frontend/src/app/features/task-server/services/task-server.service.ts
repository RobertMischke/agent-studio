import { Injectable, computed, signal } from '@angular/core';
import type {
  ManagementActionKind,
  ManagementActionResult,
  TaskServerStatus,
} from '../models/task-server.model';
import { seedTaskServerStatus } from './task-server.seed';

/**
 * Status + management-action service for the Task-Server page (AGT-1924).
 *
 * UI-first, mirroring {@link RemoteHostsService}: the status snapshot is served
 * from a static seed ({@link seedTaskServerStatus}) shaped like the future
 * `GET /api/task-server/status` endpoint. When that endpoint lands, only
 * {@link reload} changes - it swaps the seed for an HTTP call and the component
 * keeps reading `status()` unchanged. The connected URL is the one live value:
 * it is read from the serving origin so the page shows the real URL the SPA is
 * talking to.
 *
 * The Archive-sweep / Orphan-scan / Fixture-cleanup functions are applied
 * optimistically against the in-memory snapshot (there is no backend command
 * surface yet) and emit structured `task-server.*` console events so the
 * behaviour is observable in the browser log ahead of real wiring.
 */
@Injectable({ providedIn: 'root' })
export class TaskServerService {
  /** The current status snapshot, or null before the first load. */
  readonly status = signal<TaskServerStatus | null>(null);
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);
  /** Which management sweep is currently running, or null when idle. */
  readonly busyAction = signal<ManagementActionKind | null>(null);

  /** Recent management-sweep outcomes, newest first (mirrors the snapshot). */
  readonly recentResults = computed<readonly ManagementActionResult[]>(
    () => this.status()?.recentResults ?? [],
  );

  private loaded = false;

  /** Simulated latency for the optimistic sweeps (ms). */
  private static readonly ACTION_DELAY_MS = 650;

  /** Load once on first mount; explicit reloads re-read the origin + re-seed. */
  ensureLoaded(): void {
    if (this.loaded) return;
    this.reload();
  }

  reload(): void {
    this.loading.set(true);
    this.error.set(null);
    try {
      // Static snapshot for now (UI-first). This is the single line that becomes
      // an HTTP fetch once `GET /api/task-server/status` exists. The origin is
      // the real URL the SPA is served from, so the connected URL is genuine.
      const origin = this.resolveOrigin();
      // Preserve any results the operator produced this session across a reload.
      const priorResults = this.status()?.recentResults ?? [];
      const seeded = seedTaskServerStatus(Date.now(), origin);
      this.status.set({ ...seeded, recentResults: priorResults });
      this.loaded = true;
      this.log('loaded', { url: origin, clients: seeded.clients.length });
    } catch (e) {
      this.error.set('Failed to load the task-server status.');
      this.log('load-failed', { message: (e as Error)?.message ?? 'unknown' });
    } finally {
      this.loading.set(false);
    }
  }

  /** Run the Archive-sweep management function. */
  archiveSweep(): void { this.runAction('archive-sweep'); }

  /** Run the Orphan-scan management function. */
  orphanScan(): void { this.runAction('orphan-scan'); }

  /** Run the Fixture-cleanup management function. */
  fixtureCleanup(): void { this.runAction('fixture-cleanup'); }

  /**
   * Apply a management sweep optimistically: flag it busy, then commit a result
   * row after a short simulated delay. Concurrent sweeps are ignored while one
   * is in flight so the buttons cannot pile up work.
   */
  private runAction(kind: ManagementActionKind): void {
    if (this.busyAction() || !this.status()) return;

    this.busyAction.set(kind);
    this.log('action', { kind });

    setTimeout(() => {
      const snapshot = this.status();
      if (!snapshot) { this.busyAction.set(null); return; }

      const result = this.buildResult(kind, snapshot);
      this.status.set({ ...snapshot, recentResults: [result, ...snapshot.recentResults].slice(0, 6) });
      this.busyAction.set(null);
      this.log('action-applied', { kind, affected: result.affected });
    }, TaskServerService.ACTION_DELAY_MS);
  }

  /**
   * Derive a deterministic sweep outcome from the current snapshot so the same
   * store produces the same result (no `Math.random()`); the `ranAt` stamp is
   * the only live value.
   */
  private buildResult(kind: ManagementActionKind, snapshot: TaskServerStatus): ManagementActionResult {
    const store = snapshot.store;
    const ranAt = new Date().toISOString();
    switch (kind) {
      case 'archive-sweep': {
        const affected = Math.round(store.taskCount * 0.05);
        return {
          kind, ranAt, affected,
          summary: affected > 0
            ? `Swept ${affected} settled ${plural(affected, 'task', 'tasks')} into the archive.`
            : 'Nothing to sweep - no settled tasks past the archive threshold.',
        };
      }
      case 'orphan-scan': {
        const affected = 1;
        return {
          kind, ranAt, affected,
          summary: `Scanned ${store.taskCount} ${plural(store.taskCount, 'task', 'tasks')}; found ${affected} orphaned worktree with no owning task.`,
        };
      }
      case 'fixture-cleanup': {
        const affected = 0;
        return {
          kind, ranAt, affected,
          summary: 'Cleaned up 0 leftover e2e fixtures; the store is already clean.',
        };
      }
    }
  }

  /** The URL the SPA is served from; falls back to a loopback dev origin. */
  private resolveOrigin(): string {
    try {
      const origin = window.location.origin;
      if (origin && origin !== 'null') return origin;
    } catch { /* non-browser context (unit tests) */ }
    return 'http://localhost:4010';
  }

  private log(event: string, detail: Record<string, unknown>): void {
    // Stable event names so the browser log reads as a domain feed while the
    // real backend command surface is still being built.
    console.info(`[task-server] ${event}`, { event: `task-server.${event}`, ...detail });
  }
}

/** English pluralisation helper for sweep summaries. */
function plural(n: number, one: string, many: string): string {
  return n === 1 ? one : many;
}
