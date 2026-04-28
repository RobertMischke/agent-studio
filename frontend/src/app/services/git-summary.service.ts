import { Injectable, computed, signal, inject, DestroyRef } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { GitProjectSummary } from '../models/job.model';

/**
 * Shared store for the per-project git summary used by the board's tile pills.
 * Polls every 15 seconds while the app is active. The backend caches
 * server-side for ~3 s, so the actual `git status` invocations are well
 * under one per project per refresh.
 */
@Injectable({ providedIn: 'root' })
export class GitSummaryService {
  private readonly http = inject(HttpClient);
  private readonly destroyRef = inject(DestroyRef);

  private readonly summaries = signal<GitProjectSummary[]>([]);
  readonly value = this.summaries.asReadonly();

  private timer: ReturnType<typeof setInterval> | null = null;
  private subscribers = 0;

  /** Returns a computed pill state for a given project name, or null. */
  forProject(projectName: string) {
    return computed(() => this.summaries().find(s => s.projectName === projectName) ?? null);
  }

  /** Tiles call this on mount; the polling loop runs as long as anyone is listening. */
  ensurePolling(): () => void {
    if (this.subscribers === 0) {
      this.refresh();
      this.timer = setInterval(() => this.refresh(), 15_000);
    }
    this.subscribers++;
    return () => {
      this.subscribers = Math.max(0, this.subscribers - 1);
      if (this.subscribers === 0 && this.timer) {
        clearInterval(this.timer);
        this.timer = null;
      }
    };
  }

  refresh(): void {
    this.http.get<GitProjectSummary[]>('/api/git/summary').subscribe({
      next: (s) => this.summaries.set(s ?? []),
      error: () => {
        // Failures are non-fatal — keep the previous snapshot so a flaky
        // backend doesn't blank the pills mid-board.
      }
    });
  }
}
