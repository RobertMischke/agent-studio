import { Injectable, inject } from '@angular/core';
import { Observable, of, ReplaySubject } from 'rxjs';
import { JobDetail } from '../../../models/task.model';
import { JobService } from '../../../services/task.service';

/**
 * Tiny in-memory prefetch + cache for `JobDetail` payloads keyed by
 * `watchPath::id` (`jobKey`). Owns three jobs:
 *
 * 1. **Prefetch** the next 1-2 peers in the active lane-pager iteration
 *    while the user is reading the current task, so the accept → next-task
 *    navigation feels instant: when the user clicks Mark-as-Done, the
 *    detail for the next peer is already in memory and the panel
 *    re-renders without waiting for a roundtrip.
 * 2. **Coalesce** in-flight fetches: a `prefetch` while the same key has
 *    a pending response is a no-op, and a parallel `take` subscriber on
 *    the same key shares the existing response stream.
 * 3. **Stale-guard**: each cache entry stores a wall-clock timestamp; reads
 *    older than `TTL_MS` are treated as a miss so the caller fetches a
 *    fresh detail. The TTL is short on purpose - detail payloads include
 *    log / status that move under polling, and we'd rather pay one extra
 *    GET than render an obviously-stale panel.
 *
 * Not a general-purpose cache: the only entry point that populates it
 * is the lane-pager iteration's "what's next?" question, and the only
 * consumer is the triage / pager navigation path.
 */
@Injectable({ providedIn: 'root' })
export class JobDetailPrefetchService {
  private readonly jobService = inject(JobService);

  private static readonly TTL_MS = 30_000;

  private readonly cache = new Map<string, { detail: JobDetail; cachedAt: number }>();
  private readonly inFlight = new Map<string, ReplaySubject<JobDetail>>();

  private keyOf(id: string, watchPath: string): string {
    return `${watchPath}::${id}`;
  }

  /**
   * Fire-and-forget prefetch. Idempotent: skipped when the key is
   * already cached fresh, or another prefetch is in flight. Errors are
   * swallowed - prefetch is a best-effort hint, not a contract.
   */
  prefetch(id: string, watchPath: string): void {
    const key = this.keyOf(id, watchPath);
    const cached = this.cache.get(key);
    if (cached && Date.now() - cached.cachedAt < JobDetailPrefetchService.TTL_MS) return;
    if (this.inFlight.has(key)) return;

    const subject = new ReplaySubject<JobDetail>(1);
    this.inFlight.set(key, subject);
    this.jobService.getDetail(id, watchPath).subscribe({
      next: (detail) => {
        this.cache.set(key, { detail, cachedAt: Date.now() });
        subject.next(detail);
        subject.complete();
        this.inFlight.delete(key);
      },
      error: () => {
        // Treat as a soft miss; the caller's eventual real fetch will
        // surface the error if it still applies.
        subject.complete();
        this.inFlight.delete(key);
      },
    });
  }

  /**
   * Synchronous peek. Returns the cached detail when fresh, otherwise
   * null. Use this when you need the instant-paint path and have a real
   * fetch lined up as the source-of-truth fallback (the move / pager
   * paths do exactly this: peek to repaint instantly, refetch to
   * reconcile drift). Peek (not consume) so a quick back-nav or retry
   * within the same lane walk still paints instantly without firing a
   * second prefetch round.
   */
  take(id: string, watchPath: string): JobDetail | null {
    const key = this.keyOf(id, watchPath);
    const entry = this.cache.get(key);
    if (!entry) return null;
    if (Date.now() - entry.cachedAt >= JobDetailPrefetchService.TTL_MS) {
      this.cache.delete(key);
      return null;
    }
    return entry.detail;
  }

  /**
   * Observable read. Returns the cached detail when fresh (sync via
   * `of`); subscribes to an in-flight prefetch when one is pending;
   * otherwise issues a fresh GET. The result is cached on success so a
   * subsequent `take` lands the same payload without re-fetching.
   */
  getOrFetch(id: string, watchPath: string): Observable<JobDetail> {
    const cached = this.take(id, watchPath);
    if (cached) return of(cached);

    const key = this.keyOf(id, watchPath);
    const pending = this.inFlight.get(key);
    if (pending) return pending.asObservable();

    const subject = new ReplaySubject<JobDetail>(1);
    this.inFlight.set(key, subject);
    this.jobService.getDetail(id, watchPath).subscribe({
      next: (detail) => {
        this.cache.set(key, { detail, cachedAt: Date.now() });
        subject.next(detail);
        subject.complete();
        this.inFlight.delete(key);
      },
      error: (err) => {
        subject.error(err);
        this.inFlight.delete(key);
      },
    });
    return subject.asObservable();
  }

  /**
   * Drop a single entry. Use after a mutation that we know stales the
   * cached detail (e.g. the user just acted on the job - the next
   * render needs the post-mutation state, not the prefetched snapshot).
   */
  invalidate(id: string, watchPath: string): void {
    this.cache.delete(this.keyOf(id, watchPath));
  }

  /** Drop everything. Used on lane / project change. */
  clear(): void {
    this.cache.clear();
    // In-flight prefetches keep going; their results just won't be
    // consumed. Cheap enough that we don't bother aborting.
  }
}
