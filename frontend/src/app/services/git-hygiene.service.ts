import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { GitHygieneStatus } from '../features/git';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../utils/visible-interval';

/**
 * Shared store for the per-project repository hygiene snapshot used by:
 *  - the project header dirty/unpushed badge,
 *  - the job-detail review/completed hygiene strip (which augments the
 *    project shape with a `job` overlay via `forJob`).
 *
 * The backend caches server-side for ~3 s so the actual git invocations
 * are well under one per project per refresh. The frontend polls every
 * 15 s while at least one consumer is subscribed.
 */
@Injectable({ providedIn: 'root' })
export class GitHygieneService {
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);

  private readonly projectStore = signal<Record<string, GitHygieneStatus | null>>({});

  /** Returns a computed hygiene snapshot for one project, or null until first load. */
  forProject(projectName: string) {
    return computed(() => this.projectStore()[projectName] ?? null);
  }

  /**
   * Polls hygiene for one project. Returns a tear-down function so callers
   * (component effects) can stop the loop on destroy. Multiple subscribers
   * share the same in-flight fetch.
   */
  ensurePolling(projectName: string, intervalMs = 15_000): () => void {
    if (!projectName) return () => undefined;
    const tracker = this.subscribers.get(projectName) ?? { count: 0, timer: null as VisibleIntervalHandle | null };
    if (tracker.count === 0) {
      this.refresh(projectName);
      tracker.timer = setVisibleInterval(() => this.refresh(projectName), intervalMs);
    }
    tracker.count++;
    this.subscribers.set(projectName, tracker);
    return () => {
      const t = this.subscribers.get(projectName);
      if (!t) return;
      t.count = Math.max(0, t.count - 1);
      if (t.count === 0 && t.timer) {
        clearVisibleInterval(t.timer);
        t.timer = null;
        this.subscribers.delete(projectName);
      }
    };
  }

  /** Force a fresh fetch for one project. Failures keep the previous snapshot. */
  refresh(projectName: string): void {
    if (!projectName) return;
    this.http
      .get<GitHygieneStatus>(`/api/git/hygiene?project=${encodeURIComponent(projectName)}`)
      .subscribe({
        next: (s) => this.projectStore.update(prev => ({ ...prev, [projectName]: s })),
        error: () => {
          // Non-fatal; keep the previous snapshot so a flaky backend doesn't
          // blank the badge mid-board.
        }
      });
  }

  /**
   * Job-scoped fetch. Returns the latest snapshot for the job (which is the
   * project hygiene + a `job` overlay). The result is *not* cached on this
   * service: callers (the protocol-pane hygiene strip) keep their own
   * signal so they can react to start / commit / state-change events.
   */
  fetchForJob(jobId: string, watchPath: string) {
    const params = new URLSearchParams({ watchPath });
    return this.http.get<GitHygieneStatus>(
      `/api/tasks/${encodeURIComponent(jobId)}/git/hygiene?${params}`);
  }

  /**
   * POST the manual "commit accepted task evidence" action. The backend
   * runs the platform-owned commit-message path, stamps `TaskInfo.Commit`,
   * and writes a [decision] orchestrator-chat entry into the activity log.
   */
  commitAcceptedEvidence(jobId: string, watchPath: string) {
    const params = new URLSearchParams({ watchPath });
    return this.http.post<{ commit: { sha: string; shortSha: string; message: string; filesChanged: number } }>(
      `/api/tasks/${encodeURIComponent(jobId)}/git/commit-accepted-evidence?${params}`,
      {});
  }

  private subscribers = new Map<string, { count: number; timer: VisibleIntervalHandle | null }>();
}
