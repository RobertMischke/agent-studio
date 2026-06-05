import { Injectable, signal } from '@angular/core';

/** Sort modes available on the dedicated backlog triage screen. */
export type BacklogSortMode = 'newest' | 'oldest' | 'by-type';

const STORAGE_KEY = 'atp.backlog.sortMode';
const ROUTE_PATH = '/backlog';
const HASH_ROUTE = `#${ROUTE_PATH}`;

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

function normaliseProjectName(projectName: string | null | undefined): string | null {
  const trimmed = projectName?.trim();
  return trimmed ? trimmed : null;
}

function hashSegments(hash: string): string[] {
  return hash.replace(/^#/, '').split('&').filter(Boolean);
}

function isBacklogSegment(segment: string): boolean {
  return segment === ROUTE_PATH || segment.startsWith(`${ROUTE_PATH}?`);
}

function buildBacklogSegment(projectName: string | null): string {
  if (!projectName) return ROUTE_PATH;
  const params = new URLSearchParams();
  params.set('project', projectName);
  return `${ROUTE_PATH}?${params.toString()}`;
}

function projectFromBacklogSegment(segment: string | undefined): string | null {
  if (!segment) return null;
  const queryIndex = segment.indexOf('?');
  if (queryIndex < 0) return null;
  const params = new URLSearchParams(segment.slice(queryIndex + 1));
  return normaliseProjectName(params.get('project'));
}

/**
 * Scope + sort state and URL-hash bridge for the dedicated backlog triage
 * screen at `#/backlog`. The screen renders as a first-class editor tab
 * (`StudioTabKind = 'backlog'`), equivalent to Board / Epics, so the tab
 * system (`StudioTabStateService`) owns open/close/active; this service
 * only owns the deep-link hash, the scoped project, and the persisted
 * sort mode. The shell wires a `hashchange` listener that calls
 * `syncFromHash` (returns whether the URL is on `#/backlog`) so a bookmark
 * or copy-paste opens the backlog tab.
 *
 * Filter integration stays with `BoardFiltersService`, so opening the
 * triage screen automatically respects the existing project / type /
 * tag / owner filters and the user's `#filters=...` hash payload.
 */
@Injectable({ providedIn: 'root' })
export class BacklogTriageService {
  readonly scopedProject = signal<string | null>(null);
  readonly sortMode = signal<BacklogSortMode>(readPersistedSort());

  /**
   * Push `#/backlog?project=...` to the URL hash and record the scope.
   * The backlog is now a first-class editor tab (not an overlay), so the
   * host opens the tab; this method only owns the deep-link hash + scope.
   * Idempotent.
   */
  openTriage(projectName: string | null = null): void {
    const scope = normaliseProjectName(projectName);
    this.scopedProject.set(scope);
    if (typeof window !== 'undefined') {
      const hash = window.location.hash || '';
      const nextBacklog = buildBacklogSegment(scope);
      let replaced = false;
      const nextSegments = hashSegments(hash).map(segment => {
        if (!isBacklogSegment(segment)) return segment;
        replaced = true;
        return nextBacklog;
      });
      if (!replaced) nextSegments.unshift(nextBacklog);
      const nextHash = nextSegments.join('&');
      const target = window.location.pathname + window.location.search + `#${nextHash}`;
      if (target !== window.location.pathname + window.location.search + hash) {
        try {
          history.pushState(null, '', target);
        } catch {
          /* ignore */
        }
      }
    }
  }

  /** Clear `#/backlog` from the URL hash and reset the scope. Idempotent. */
  closeTriage(): void {
    if (typeof window !== 'undefined') {
      const hash = window.location.hash || '';
      if (hash.startsWith(HASH_ROUTE) || hash.includes('/backlog')) {
        const others = hashSegments(hash).filter(s => !isBacklogSegment(s));
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
    this.scopedProject.set(null);
  }

  /**
   * Read the current hash and reconcile the scope signal. Returns whether
   * the URL is currently on the `#/backlog` deep link so the host can open
   * (or focus) the backlog tab.
   */
  syncFromHash(projectName: string | null = null): boolean {
    if (typeof window === 'undefined') return false;
    const hash = window.location.hash || '';
    const backlogSegment = hashSegments(hash).find(isBacklogSegment);
    const onBacklog = hash.startsWith(HASH_ROUTE) || backlogSegment !== undefined;
    if (onBacklog) {
      this.scopedProject.set(projectFromBacklogSegment(backlogSegment) ?? normaliseProjectName(projectName));
    } else {
      this.scopedProject.set(null);
    }
    return onBacklog;
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
