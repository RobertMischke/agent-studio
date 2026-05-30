import { Injectable, inject } from '@angular/core';
import { Observable, ReplaySubject, of } from 'rxjs';
import { tap } from 'rxjs/operators';
import { TaskService } from '../../../services/task.service';
import type { RunFilesResponse, RunDiffResponse } from '../../run-timeline';

/**
 * In-memory cache for the SHA-range answers behind the run git viewer:
 * `GET /api/tasks/{id}/runs/{i}/files` and
 * `GET /api/tasks/{id}/runs/{i}/diff?path=...`.
 *
 * Why this exists: clicking back into a previously-visible run used to
 * re-spawn the backend `git diff`. The backend now memoises the SHA
 * range too (`GitService` LRU), but the frontend round-trip still costs
 * an HTTP queue slot + JSON parse. Caching the response on the browser
 * side makes a re-open of the same run a single map lookup (target:
 * sub-30ms on a warm cache, same as the TaskDetail prefetch path).
 *
 * Invalidation:
 *   - `invalidate(jobId, watchPath)` drops every entry for a job after
 *     a mutation that may have advanced the run set (start / continue /
 *     stop, lane move, manual summary regen).
 *   - `clear()` wipes everything on lane / project change.
 *
 * SHA ranges themselves are content-addressed, so for a finished run the
 * answer truly never changes — we could keep entries forever — but a
 * small bounded LRU is friendlier to long sessions, and matches the
 * shape of the existing `TaskDetailPrefetchService`.
 */
@Injectable({ providedIn: 'root' })
export class RunGitCacheService {
  private readonly jobService = inject(TaskService);

  private static readonly TTL_MS = 60_000;
  private static readonly LIMIT = 128;

  private readonly files = new Map<string, { value: RunFilesResponse; at: number }>();
  private readonly diffs = new Map<string, { value: RunDiffResponse; at: number }>();
  private readonly filesInFlight = new Map<string, ReplaySubject<RunFilesResponse>>();
  private readonly diffsInFlight = new Map<string, ReplaySubject<RunDiffResponse>>();

  getFiles(jobId: string, runIndex: number, watchPath: string): Observable<RunFilesResponse> {
    const key = `${watchPath}::${jobId}::${runIndex}`;
    const cached = this.takeFiles(key);
    if (cached) return of(cached);
    const pending = this.filesInFlight.get(key);
    if (pending) return pending.asObservable();
    const subject = new ReplaySubject<RunFilesResponse>(1);
    this.filesInFlight.set(key, subject);
    return this.jobService.getRunFiles(jobId, runIndex, watchPath).pipe(
      tap({
        next: (res) => {
          this.storeFiles(key, res);
          subject.next(res);
          subject.complete();
          this.filesInFlight.delete(key);
        },
        error: () => {
          subject.complete();
          this.filesInFlight.delete(key);
        },
      }),
    );
  }

  getDiff(
    jobId: string,
    runIndex: number,
    path: string,
    watchPath: string,
  ): Observable<RunDiffResponse> {
    const key = `${watchPath}::${jobId}::${runIndex}::${path}`;
    const cached = this.takeDiff(key);
    if (cached) return of(cached);
    const pending = this.diffsInFlight.get(key);
    if (pending) return pending.asObservable();
    const subject = new ReplaySubject<RunDiffResponse>(1);
    this.diffsInFlight.set(key, subject);
    return this.jobService.getRunDiff(jobId, runIndex, path, watchPath).pipe(
      tap({
        next: (res) => {
          this.storeDiff(key, res);
          subject.next(res);
          subject.complete();
          this.diffsInFlight.delete(key);
        },
        error: () => {
          subject.complete();
          this.diffsInFlight.delete(key);
        },
      }),
    );
  }

  /** Drop every cached entry whose key starts with this job. */
  invalidate(jobId: string, watchPath: string): void {
    const prefix = `${watchPath}::${jobId}::`;
    for (const k of [...this.files.keys()]) if (k.startsWith(prefix)) this.files.delete(k);
    for (const k of [...this.diffs.keys()]) if (k.startsWith(prefix)) this.diffs.delete(k);
  }

  clear(): void {
    this.files.clear();
    this.diffs.clear();
  }

  private takeFiles(key: string): RunFilesResponse | null {
    const entry = this.files.get(key);
    if (!entry) return null;
    if (Date.now() - entry.at >= RunGitCacheService.TTL_MS) {
      this.files.delete(key);
      return null;
    }
    return entry.value;
  }

  private takeDiff(key: string): RunDiffResponse | null {
    const entry = this.diffs.get(key);
    if (!entry) return null;
    if (Date.now() - entry.at >= RunGitCacheService.TTL_MS) {
      this.diffs.delete(key);
      return null;
    }
    return entry.value;
  }

  private storeFiles(key: string, value: RunFilesResponse): void {
    this.files.set(key, { value, at: Date.now() });
    while (this.files.size > RunGitCacheService.LIMIT) {
      const oldest = this.files.keys().next();
      if (oldest.done) break;
      this.files.delete(oldest.value);
    }
  }

  private storeDiff(key: string, value: RunDiffResponse): void {
    this.diffs.set(key, { value, at: Date.now() });
    while (this.diffs.size > RunGitCacheService.LIMIT) {
      const oldest = this.diffs.keys().next();
      if (oldest.done) break;
      this.diffs.delete(oldest.value);
    }
  }
}
