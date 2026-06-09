import { Injectable, computed, inject, signal } from '@angular/core';
import { Observable, ReplaySubject, of } from 'rxjs';
import { catchError, finalize, tap } from 'rxjs/operators';
import { CLI_TYPES, type CliType } from '../models/task.model';
import type { CliModelCatalog, CliModelInfo } from '../features/cli';
import { TaskService } from './task.service';

interface CatalogEntry {
  models: readonly CliModelInfo[];
  source: string;
  fetchedAt: number;
}

/**
 * Process-wide cache of the per-CLI model catalog (`GET /api/cli/{type}/models`).
 *
 * Hydrated at app boot via `hydrateAll()` so the chat-model badge and
 * status-bar pickers open without a network round-trip. Entries older than
 * `TTL_MS` are refreshed in the background on the next consumer read, but
 * the stale value is still returned synchronously so the UI never blocks
 * on the wire. Explicit refresh is triggered via `refresh(cliType)` (e.g.
 * the status-bar's "Refresh catalog" menu row).
 *
 * Companion to [[ADR-0046]] — Optimistic-UI + client-side catalog cache.
 */
@Injectable({ providedIn: 'root' })
export class CliCatalogStore {
  private static readonly TTL_MS = 60 * 60 * 1000; // 1h
  private static readonly OPEN_REFRESH_COOLDOWN_MS = 5 * 60 * 1000; // 5 min

  private readonly jobs = inject(TaskService);
  private readonly entries = signal<ReadonlyMap<CliType, CatalogEntry>>(new Map());
  private readonly inFlight = new Map<string, ReplaySubject<readonly CliModelInfo[]>>();
  private readonly pickerOpenRefreshAt = new Map<CliType, number>();

  /** Live signal of every CLI's cached model list. Consumers should prefer `modelsFor(cliType)`. */
  readonly catalogs = this.entries.asReadonly();

  /**
   * Returns the cached model list for one CLI (empty array when nothing is
   * cached yet). Reactive — re-emits when `hydrate` or `refresh` updates the
   * underlying map.
   */
  modelsFor(cliType: CliType): readonly CliModelInfo[] {
    return this.entries().get(cliType)?.models ?? [];
  }

  /** Returns true when a fresh-enough entry is cached for the given CLI. */
  hasFresh(cliType: CliType): boolean {
    const entry = this.entries().get(cliType);
    if (!entry) return false;
    return Date.now() - entry.fetchedAt < CliCatalogStore.TTL_MS;
  }

  /**
   * Pre-fetches every known CLI's model catalog in parallel. Safe to call
   * multiple times — re-uses in-flight requests and skips entries that are
   * still within TTL. Errors are swallowed per CLI so one broken backend
   * (e.g. copilot CLI uninstalled) doesn't block the others.
   */
  hydrateAll(): void {
    for (const cli of CLI_TYPES) {
      if (this.hasFresh(cli) || this.hasInFlight(cli)) continue;
      this.fetch(cli, false).subscribe({ error: () => void 0 });
    }
  }

  /**
   * Returns an observable that resolves to the catalog for one CLI. When a
   * fresh entry exists, completes synchronously with the cached value.
   * Otherwise dedupes in-flight requests so concurrent callers all observe
   * the same response.
   */
  ensure(cliType: CliType): Observable<readonly CliModelInfo[]> {
    if (this.hasFresh(cliType)) {
      return of(this.modelsFor(cliType));
    }
    return this.fetch(cliType, false);
  }

  /** Force-refresh the catalog (bypasses TTL). Used by explicit "Refresh catalog" UI rows. */
  refresh(cliType: CliType): Observable<readonly CliModelInfo[]> {
    return this.fetch(cliType, true);
  }

  /**
   * Bounded automatic refresh for picker opens. The selector still renders the
   * cached catalog immediately, then asks the backend for a forced refresh at
   * most once per CLI per cooldown window so already-open browser tabs see
   * newly deployed capability data without hammering PTY discovery.
   */
  refreshForPickerOpen(cliType: CliType): Observable<readonly CliModelInfo[]> | null {
    const now = Date.now();
    const previous = this.pickerOpenRefreshAt.get(cliType);
    if (previous !== undefined && now - previous < CliCatalogStore.OPEN_REFRESH_COOLDOWN_MS) return null;
    this.pickerOpenRefreshAt.set(cliType, now);
    return this.refresh(cliType);
  }

  /** Drops every cached entry. Reserved for SignalR `CatalogChanged` invalidation. */
  invalidateAll(): void {
    this.entries.set(new Map());
  }

  /** Drops the cached entry for one CLI. Reserved for targeted invalidation. */
  invalidate(cliType: CliType): void {
    const next = new Map(this.entries());
    next.delete(cliType);
    this.entries.set(next);
  }

  /** Number of CLIs currently cached — handy for boot-readiness derived signals. */
  readonly cachedCount = computed(() => this.entries().size);

  private fetch(cliType: CliType, force: boolean): Observable<readonly CliModelInfo[]> {
    const key = this.inFlightKey(cliType, force);
    const existing = this.inFlight.get(key);
    if (existing) return existing.asObservable();

    const subject = new ReplaySubject<readonly CliModelInfo[]>(1);
    this.inFlight.set(key, subject);

    this.jobs
      .getCliModelCatalog(cliType, force)
      .pipe(
        tap((catalog: CliModelCatalog) => {
          const next = new Map(this.entries());
          next.set(cliType, {
            models: catalog.models ?? [],
            source: catalog.source ?? '',
            fetchedAt: Date.now(),
          });
          this.entries.set(next);
        }),
        catchError((err: unknown) => {
          subject.error(err);
          return of(null);
        }),
        finalize(() => {
          this.inFlight.delete(key);
        }),
      )
      .subscribe({
        next: (catalog) => {
          if (catalog) {
            subject.next(catalog.models ?? []);
            subject.complete();
          }
        },
      });

    return subject.asObservable();
  }

  private hasInFlight(cliType: CliType): boolean {
    return this.inFlight.has(this.inFlightKey(cliType, false))
      || this.inFlight.has(this.inFlightKey(cliType, true));
  }

  private inFlightKey(cliType: CliType, force: boolean): string {
    return `${cliType}:${force ? 'force' : 'normal'}`;
  }
}
