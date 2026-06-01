import { Injectable, signal } from '@angular/core';

/**
 * In-memory registry of tasks that currently have a user-triggered
 * code-review pass in flight. The code-review panel marks a task running
 * when it POSTs the review and clears it when the synchronous call
 * resolves; the kanban card reads the same singleton to render a "code
 * review running" badge even while the operator is on a different screen.
 *
 * Deliberately not persisted: a review is a synchronous request that lives
 * for the lifetime of one panel call, so a page reload mid-run simply drops
 * the ephemeral badge rather than leaving a phantom "running" marker behind.
 * Keyed by {@link key} (watchPath + task id) because a {@link TaskInfo} has
 * no stable single-field identity once the same id can appear under
 * different watch roots.
 */
@Injectable({ providedIn: 'root' })
export class CodeReviewActivityStore {
  private readonly active = signal<ReadonlySet<string>>(new Set<string>());

  /** Compose the registry key for a task from its watch root + id. */
  static key(watchPath: string | null | undefined, jobId: string): string {
    return `${watchPath ?? ''}::${jobId}`;
  }

  markRunning(key: string): void {
    const cur = this.active();
    if (cur.has(key)) return;
    const next = new Set(cur);
    next.add(key);
    this.active.set(next);
  }

  clear(key: string): void {
    const cur = this.active();
    if (!cur.has(key)) return;
    const next = new Set(cur);
    next.delete(key);
    this.active.set(next);
  }

  isRunning(key: string): boolean {
    return this.active().has(key);
  }
}
