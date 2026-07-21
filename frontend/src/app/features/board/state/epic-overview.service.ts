import { Injectable, signal } from '@angular/core';
import { routeSegmentOf, withRouteSegment } from '../../../services/url-hash.util';

const EPICS_ROUTE = '/epics';

/**
 * State + URL-hash sync for the dedicated epic overview screen at
 * `#/epics`. A signal holds the
 * open/close state, the shell wires a `hashchange` listener that calls
 * `syncFromHash`, and imperative open/close mutate both the hash and the
 * signal. `/epics` is a hash ROUTE SEGMENT (url-hash.util.ts): it replaces
 * any other overlay route on open and coexists with `filters=...`.
 *
 * The screen itself is read-only and sources its data live from
 * `GET /api/epics`, so there is no per-epic state to persist here.
 */
@Injectable({ providedIn: 'root' })
export class EpicOverviewService {
  readonly open = signal(false);

  /** Push `#/epics` and flip the overlay open. Idempotent. */
  openOverview(): void {
    if (typeof window !== 'undefined' && !this.isEpicsRoute(routeSegmentOf(window.location.hash))) {
      const target = withRouteSegment(window.location.hash, EPICS_ROUTE);
      try {
        history.pushState(null, '', window.location.pathname + window.location.search + target);
      } catch {
        /* ignore */
      }
    }
    if (!this.open()) this.open.set(true);
  }

  /** Clear `#/epics` and flip the overlay closed. Idempotent. */
  closeOverview(): void {
    if (typeof window !== 'undefined' && this.isEpicsRoute(routeSegmentOf(window.location.hash))) {
      const target = withRouteSegment(window.location.hash, null);
      try {
        history.pushState(null, '', window.location.pathname + window.location.search + target);
      } catch {
        /* ignore */
      }
    }
    if (this.open()) this.open.set(false);
  }

  /** Read the current hash and reconcile the open signal. */
  syncFromHash(): void {
    if (typeof window === 'undefined') return;
    const onEpics = this.isEpicsRoute(routeSegmentOf(window.location.hash));
    if (onEpics !== this.open()) this.open.set(onEpics);
  }

  private isEpicsRoute(route: string | null): boolean {
    return route === EPICS_ROUTE || (route?.startsWith(`${EPICS_ROUTE}?`) ?? false);
  }
}
