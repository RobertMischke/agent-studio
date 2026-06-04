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
  readonly scopedProject = signal<string | null>(null);
  readonly sortMode = signal<BacklogSortMode>(readPersistedSort());

  /** Push `#/backlog?project=...` and flip the overlay open. Idempotent. */
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
    if (!this.open()) this.open.set(true);
  }

  /** Clear `#/backlog` and flip the overlay closed. Idempotent. */
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
    if (this.open()) this.open.set(false);
  }

  /** Read the current hash and reconcile the open signal. */
  syncFromHash(projectName: string | null = null): void {
    if (typeof window === 'undefined') return;
    const hash = window.location.hash || '';
    const backlogSegment = hashSegments(hash).find(isBacklogSegment);
    const onBacklog = hash.startsWith(HASH_ROUTE) || backlogSegment !== undefined;
    if (onBacklog) {
      this.scopedProject.set(projectFromBacklogSegment(backlogSegment) ?? normaliseProjectName(projectName));
    } else {
      this.scopedProject.set(null);
    }
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
