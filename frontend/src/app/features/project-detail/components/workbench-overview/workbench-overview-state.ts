import { signal } from '@angular/core';
import type {
  WorkbenchListItem,
  WorkbenchOverviewItem,
} from '../../../../models/project-docs.model';
import { routeSegmentOf, withRouteSegment } from '../../../../services/url-hash.util';

export type WorkbenchOverviewSortKey =
  | 'default'
  | 'status'
  | 'updated'
  | 'project'
  | 'key'
  | 'decisions';
export type WorkbenchOverviewSortDirection = 'asc' | 'desc';

export interface WorkbenchOverviewViewOptions {
  query: string;
  sortKey: WorkbenchOverviewSortKey;
  direction: WorkbenchOverviewSortDirection;
}

export const WORKBENCH_OVERVIEW_SESSION_KEY = 'atp.workbenches.overview.view.v1';

const DEFAULT_OPTIONS: WorkbenchOverviewViewOptions = {
  query: '',
  sortKey: 'default',
  direction: 'asc',
};
const SORT_KEYS = new Set<WorkbenchOverviewSortKey>([
  'default',
  'status',
  'updated',
  'project',
  'key',
  'decisions',
]);
const COLLATOR = new Intl.Collator(undefined, { numeric: true, sensitivity: 'base' });

export class WorkbenchOverviewViewState {
  readonly query = signal('');
  readonly sortKey = signal<WorkbenchOverviewSortKey>('default');
  readonly direction = signal<WorkbenchOverviewSortDirection>('asc');

  hydrate(): void {
    const fromUrl = typeof window === 'undefined'
      ? null
      : readWorkbenchOverviewRouteState(window.location.hash);
    this.apply(fromUrl ?? readSessionState() ?? DEFAULT_OPTIONS);
  }

  setQuery(query: string): void {
    this.query.set(query);
    this.persist();
  }

  selectSort(sortKey: WorkbenchOverviewSortKey): void {
    if (sortKey === 'default') {
      this.sortKey.set('default');
      this.direction.set('asc');
    } else if (this.sortKey() === sortKey) {
      this.direction.update(value => value === 'asc' ? 'desc' : 'asc');
    } else {
      this.sortKey.set(sortKey);
      this.direction.set(defaultDirection(sortKey));
    }
    this.persist();
  }

  private apply(options: WorkbenchOverviewViewOptions): void {
    this.query.set(options.query);
    this.sortKey.set(options.sortKey);
    this.direction.set(options.sortKey === 'default' ? 'asc' : options.direction);
  }

  private persist(): void {
    const options: WorkbenchOverviewViewOptions = {
      query: this.query(),
      sortKey: this.sortKey(),
      direction: this.direction(),
    };
    writeSessionState(options);
    if (typeof window === 'undefined') return;
    const nextHash = writeWorkbenchOverviewRouteState(window.location.hash, options);
    if (nextHash === window.location.hash) return;
    window.history.replaceState(
      null,
      '',
      window.location.pathname + window.location.search + nextHash,
    );
  }
}

export function projectWorkbenchOverviewItems(
  items: readonly WorkbenchOverviewItem[],
  options: WorkbenchOverviewViewOptions,
): WorkbenchOverviewItem[] {
  const query = normalize(options.query);
  const visible = query
    ? items.filter(item => searchableText(item).includes(query))
    : [...items];
  const sortKey = options.sortKey;
  if (sortKey === 'default') return visible;

  return visible
    .map((item, index) => ({ item, index }))
    .sort((left, right) => {
      const compared = compareItems(left.item, right.item, sortKey);
      if (compared === 0) return left.index - right.index;
      return options.direction === 'asc' ? compared : -compared;
    })
    .map(entry => entry.item);
}

export function workbenchOverviewStatusLabel(workbench: WorkbenchListItem): string {
  if (!workbench.valid) return 'Needs attention';
  if (workbench.documentation?.eligible) return 'Ready to document';
  if (workbench.status === 'decision-pending') return 'Decision pending';
  if (workbench.status === 'active') return workbench.phase ?? 'Active';
  if (workbench.status === 'decided') return 'Tracking';
  if (workbench.status === 'archived') return 'Discarded';
  if (workbench.status === 'documented') return 'Documented';
  return workbench.status;
}

export function workbenchOverviewKey(item: WorkbenchOverviewItem): string {
  return item.workbench.key?.trim() || item.workbench.id;
}

export function readWorkbenchOverviewRouteState(
  hash: string,
): WorkbenchOverviewViewOptions | null {
  const route = routeSegmentOf(hash);
  if (!route) return null;
  const queryIndex = route.indexOf('?');
  const path = queryIndex >= 0 ? route.slice(0, queryIndex) : route;
  if (!isOverviewPath(path)) return null;
  const query = new URLSearchParams(queryIndex >= 0 ? route.slice(queryIndex + 1) : '');
  if (!query.has('view')) return null;
  const view = new URLSearchParams(query.get('view') ?? '');

  const requestedSortKey = view.get('sort');
  const sortKey = isSortKey(requestedSortKey) ? requestedSortKey : 'default';
  const requestedDirection = view.get('dir');
  const direction = requestedDirection === 'asc' || requestedDirection === 'desc'
    ? requestedDirection
    : defaultDirection(sortKey);
  return {
    query: view.get('q') ?? '',
    sortKey,
    direction: sortKey === 'default' ? 'asc' : direction,
  };
}

export function writeWorkbenchOverviewRouteState(
  hash: string,
  options: WorkbenchOverviewViewOptions,
): string {
  const route = routeSegmentOf(hash);
  if (!route) return hash;
  const queryIndex = route.indexOf('?');
  const path = queryIndex >= 0 ? route.slice(0, queryIndex) : route;
  if (!isOverviewPath(path)) return hash;
  const view = new URLSearchParams();
  setQueryValue(view, 'q', options.query.trim() || null);
  setQueryValue(view, 'sort', options.sortKey === 'default' ? null : options.sortKey);
  setQueryValue(view, 'dir', options.sortKey === 'default' ? null : options.direction);
  const query = new URLSearchParams();
  setQueryValue(query, 'view', view.size > 0 ? view.toString() : null);
  const suffix = query.toString();
  return withRouteSegment(hash, suffix ? `${path}?${suffix}` : path);
}

function compareItems(
  left: WorkbenchOverviewItem,
  right: WorkbenchOverviewItem,
  sortKey: Exclude<WorkbenchOverviewSortKey, 'default'>,
): number {
  switch (sortKey) {
    case 'status':
      return COLLATOR.compare(
        workbenchOverviewStatusLabel(left.workbench),
        workbenchOverviewStatusLabel(right.workbench),
      );
    case 'updated':
      return timestamp(left.workbench.updatedAtUtc) - timestamp(right.workbench.updatedAtUtc);
    case 'project':
      return COLLATOR.compare(left.projectName, right.projectName);
    case 'key':
      return COLLATOR.compare(workbenchOverviewKey(left), workbenchOverviewKey(right));
    case 'decisions':
      return openDecisionCount(left) - openDecisionCount(right);
  }
}

function searchableText(item: WorkbenchOverviewItem): string {
  return normalize([
    workbenchOverviewKey(item),
    item.workbench.title,
    item.projectName,
    workbenchOverviewStatusLabel(item.workbench),
  ].join('\n'));
}

function openDecisionCount(item: WorkbenchOverviewItem): number {
  return item.workbench.openDecisionCount
    ?? (item.workbench.status === 'decision-pending' ? 1 : 0);
}

function timestamp(value: string): number {
  const parsed = new Date(value).getTime();
  return Number.isFinite(parsed) ? parsed : 0;
}

function defaultDirection(sortKey: WorkbenchOverviewSortKey): WorkbenchOverviewSortDirection {
  return sortKey === 'updated' || sortKey === 'decisions' ? 'desc' : 'asc';
}

function isSortKey(value: string | null): value is WorkbenchOverviewSortKey {
  return value !== null && SORT_KEYS.has(value as WorkbenchOverviewSortKey);
}

function isOverviewPath(path: string): boolean {
  return path === '/workbenches' || /^\/projects\/[^/]+\/workbenches$/.test(path);
}

function normalize(value: string): string {
  return value.trim().toLocaleLowerCase();
}

function setQueryValue(query: URLSearchParams, key: string, value: string | null): void {
  if (value === null) query.delete(key);
  else query.set(key, value);
}

function readSessionState(): WorkbenchOverviewViewOptions | null {
  if (typeof window === 'undefined') return null;
  try {
    const parsed = JSON.parse(window.sessionStorage.getItem(WORKBENCH_OVERVIEW_SESSION_KEY) ?? 'null') as
      Partial<WorkbenchOverviewViewOptions> | null;
    const sortKey = parsed?.sortKey ?? null;
    if (!parsed || typeof parsed.query !== 'string' || !isSortKey(sortKey)) return null;
    return {
      query: parsed.query,
      sortKey,
      direction: parsed.direction === 'desc' ? 'desc' : 'asc',
    };
  } catch {
    return null;
  }
}

function writeSessionState(options: WorkbenchOverviewViewOptions): void {
  if (typeof window === 'undefined') return;
  try {
    window.sessionStorage.setItem(WORKBENCH_OVERVIEW_SESSION_KEY, JSON.stringify(options));
  } catch {
    // Session storage can be unavailable in restricted or private browser contexts.
  }
}
