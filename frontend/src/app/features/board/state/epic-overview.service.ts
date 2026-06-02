import { Injectable, signal } from '@angular/core';

const HASH_ROUTE = '#/epics';

/**
 * State + URL-hash sync for the dedicated epic overview screen at
 * `#/epics`. Mirrors {@link BacklogTriageService}: a signal holds the
 * open/close state, the shell wires a `hashchange` listener that calls
 * `syncFromHash`, and imperative open/close mutate both the hash and the
 * signal.
 *
 * The screen itself is read-only and sources its data live from
 * `GET /api/epics`, so there is no per-epic state to persist here.
 */
@Injectable({ providedIn: 'root' })
export class EpicOverviewService {
  readonly open = signal(false);

  /** Push `#/epics` and flip the overlay open. Idempotent. */
  openOverview(): void {
    if (typeof window !== 'undefined') {
      const hash = window.location.hash || '';
      if (!hash.startsWith(HASH_ROUTE)) {
        const others = hash.replace(/^#/, '')
          .split('&')
          .filter(s => s && !s.startsWith('/epics'));
        const next = ['/epics', ...others].filter(Boolean).join('&');
        try {
          history.pushState(null, '', window.location.pathname + window.location.search + `#${next}`);
        } catch {
          /* ignore */
        }
      }
    }
    if (!this.open()) this.open.set(true);
  }

  /** Clear `#/epics` and flip the overlay closed. Idempotent. */
  closeOverview(): void {
    if (typeof window !== 'undefined') {
      const hash = window.location.hash || '';
      if (hash.startsWith(HASH_ROUTE) || hash.includes('/epics')) {
        const others = hash.replace(/^#/, '')
          .split('&')
          .filter(s => s && !s.startsWith('/epics'));
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
    const onEpics = hash.startsWith(HASH_ROUTE)
      || hash.split('&').some(s => s === '/epics' || s.startsWith('/epics?'));
    if (onEpics !== this.open()) this.open.set(onEpics);
  }
}
