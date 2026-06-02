import { Injectable, signal } from '@angular/core';

/** Sort modes available on the dedicated backlog triage screen. */
export type BacklogSortMode = 'newest' | 'oldest' | 'by-type';

const STORAGE_KEY = 'atp.backlog.sortMode';
const HASH_ROUTE = '#/backlog';

function isBacklogSortMode(value: unknown): value is BacklogSortMode {
  return value === 'newest' || value === 'oldest' || value === 'by-type';
}

function readPersistedSort(): BacklogSortMode {
  if (typeof window === 'undefined') return 'newest';
  try {
    const raw = window.localStorage?.getItem(STORAGE_KEY);
    return isBacklogSortMode(raw) ? raw : 'newest';
  } catch {
    return 'newest';
  }
}

/**
 * State + URL-hash sync for the dedicated backlog triage screen at
 * `#/backlog`. Mirrors the pattern used by `ProjectOverlaysService` for
 * `#/projects/<slug>`: signals hold the open/close + sort state, the
 * shell wires a `hashchange` listener that calls `syncFromHash`, and
 * imperative open/close mutate both the hash and the signal.
 *
 * Filter integration stays with `BoardFiltersService`, so opening the
 * triage screen automatically respects the existing project / type /
 * tag / owner filters and the user's `#filters=...` hash payload.
 */
@Injectable({ providedIn: 'root' })
export class BacklogTriageService {
  readonly open = signal(false);
  readonly sortMode = signal<BacklogSortMode>(readPersistedSort());

  /** Push `#/backlog` and flip the overlay open. Idempotent. */
  openTriage(): void {
    if (typeof window !== 'undefined') {
      const hash = window.location.hash || '';
      if (!hash.startsWith(HASH_ROUTE)) {
        const others = hash.replace(/^#/, '')
          .split('&')
          .filter(s => s && !s.startsWith('/backlog'));
        const next = ['/backlog', ...others].filter(Boolean).join('&');
        try {
          history.pushState(null, '', window.location.pathname + window.location.search + `#${next}`);
        } catch {
          /* ignore */
        }
      }
    }
    if (!this.open()) this.open.set(true);
  }

  /** Clear `#/backlog` and flip the overlay closed. Idempotent. */
  closeTriage(): void {
    if (typeof window !== 'undefined') {
      const hash = window.location.hash || '';
      if (hash.startsWith(HASH_ROUTE) || hash.includes('/backlog')) {
        const others = hash.replace(/^#/, '')
          .split('&')
          .filter(s => s && !s.startsWith('/backlog'));
        const next = others.join('&');
        const target = next
          ? window.location.pathname + window.location.search + `#${next}`
          : window.location.pathname + window.location.search;
        try {
          history.pushState(null, '', target);
        } catch {
          /* ignore */
        }
      }
    }
    if (this.open()) this.open.set(false);
  }

  /** Read the current hash and reconcile the open signal. */
  syncFromHash(): void {
    if (typeof window === 'undefined') return;
    const hash = window.location.hash || '';
    const onBacklog = hash.startsWith(HASH_ROUTE)
      || hash.split('&').some(s => s === '/backlog' || s.startsWith('/backlog?'));
    if (onBacklog !== this.open()) this.open.set(onBacklog);
  }

  setSortMode(mode: BacklogSortMode): void {
    this.sortMode.set(mode);
    if (typeof window !== 'undefined') {
      try {
        window.localStorage?.setItem(STORAGE_KEY, mode);
      } catch {
        /* storage may be blocked */
      }
    }
  }
}
