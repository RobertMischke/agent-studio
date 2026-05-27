import { Injectable, computed, inject, signal } from '@angular/core';
import { GroupedJobs, JobInfo } from '../../../models/task.model';
import { JobService } from '../../../services/task.service';
import { ClientService } from '../../../services/client.service';
import { TagRegistryStore } from '../../../services/tag-registry.store';
import { projectIdentity } from '../../../services/project-identity.util';

/**
 * Cycle 9 board feature service: free-text search query, four faceted
 * filter axes (owner / project / type / tags), the URL-hash + query-param
 * round-trip, and the `filteredGrouped` derivation that the kanban
 * shell binds. Lifted out of `app.ts` per ADR-0034 so the shell stays a
 * thin coordinator and the filter state machine has one grep target.
 *
 * Contracts preserved from the pre-extraction shell:
 *
 *   - `activeProjects`           localStorage `activeProjects` (string[])
 *   - `#filters=owner:..;projects:..;type:..;tags:..`  URL hash
 *   - `?q=..&owner=..&type=..&tag=..`                  URL query
 *   - Tag filter is multi-select with AND semantics.
 *   - Type filter is single-select; the Set wrapper stays for shape
 *     compatibility but never holds more than one entry.
 *
 * The legacy `#filter=type:..,tag:..` hash is still honoured on read so
 * old bookmarks keep working.
 */

export interface ActiveFilterPill {
  kind: 'owner' | 'project' | 'type' | 'tag';
  kindLabel: string;
  /** Identifier used by the remove handler. */
  value: string;
  /** Visible label shown on the pill. */
  label: string;
  /** Optional CSS colour for the leading swatch (tag colour, project colour). */
  swatch: string | null;
}

@Injectable({ providedIn: 'root' })
export class BoardFiltersService {
  private readonly jobService = inject(JobService);
  private readonly clientService = inject(ClientService);
  private readonly tagRegistryStore = inject(TagRegistryStore);

  // ---------- raw filter state ----------

  readonly searchQuery = signal<string>('');

  readonly activeProjects = signal<Set<string>>(
    new Set(safeParseStringArray(localStorage.getItem('activeProjects')))
  );

  /** null = no filter; otherwise show only jobs whose ownerClientId matches. */
  readonly activeClientFilter = signal<string | null>(null);

  readonly activeTypeFilter = signal<Set<string>>(new Set());
  readonly activeTagFilter = signal<Set<string>>(new Set());

  /** Single-select view of the type filter. Null = no type filter. */
  readonly activeType = computed<string | null>(() => {
    const s = this.activeTypeFilter();
    if (s.size === 0) return null;
    return s.values().next().value as string;
  });

  // ---------- derived banners / counters ----------

  readonly bannerProjects = computed<readonly string[]>(() => [...this.activeProjects()]);

  readonly hasActiveFilters = computed(() =>
    this.activeTypeFilter().size > 0
    || this.activeTagFilter().size > 0
    || !!this.activeClientFilter()
    || this.activeProjects().size > 0);

  readonly hasActiveFiltersOrSearch = computed(() =>
    this.searchQuery().trim().length > 0
    || this.activeClientFilter() !== null
    || this.activeProjects().size > 0
    || this.activeType() !== null
    || this.activeTagFilter().size > 0);

  readonly activeFilterCount = computed(() => {
    let n = 0;
    if (this.activeClientFilter()) n += 1;
    n += this.activeProjects().size;
    if (this.activeType()) n += 1;
    n += this.activeTagFilter().size;
    return n;
  });

  // ---------- filtered grouped feed ----------

  readonly filteredGrouped = computed(() => {
    const grouped = this.jobService.grouped();
    const active = this.activeProjects();
    const ownerId = this.activeClientFilter();
    const types = this.activeTypeFilter();
    const tagIds = this.activeTagFilter();
    const query = this.searchQuery().trim().toLowerCase();
    const noFilters = active.size === 0 && !ownerId && types.size === 0 && tagIds.size === 0 && !query;
    if (noFilters) return grouped;
    const matchesQuery = (j: JobInfo) => {
      if (!query) return true;
      const haystack = [
        j.title, j.id, j.projectName, j.agent,
        j.model ?? '', j.cliType ?? '', j.sessionName ?? '', j.state,
        j.ownerClientId ?? '', j.phase ?? '', j.taskType ?? '',
        ...(j.tags ?? []),
      ].join(' ').toLowerCase();
      return haystack.includes(query);
    };
    const filterJobs = (jobs: JobInfo[]) => jobs.filter(j => {
      if (active.size > 0 && !active.has(j.projectName)) return false;
      if (ownerId && j.ownerClientId !== ownerId) return false;
      if (types.size > 0) {
        const t = j.taskType || 'chore';
        if (!types.has(t)) return false;
      }
      if (tagIds.size > 0) {
        const jobTags = new Set(j.tags ?? []);
        for (const tid of tagIds) if (!jobTags.has(tid)) return false;
      }
      if (!matchesQuery(j)) return false;
      return true;
    });
    const autoReviewFiltered = filterJobs(grouped.autoReview ?? grouped.review);
    return {
      backlog: filterJobs(grouped.backlog ?? []),
      preparation: filterJobs(grouped.preparation),
      orchestratorPrep: filterJobs(grouped.orchestratorPrep ?? []),
      needsHumanReview: filterJobs(grouped.needsHumanReview ?? []),
      ready: filterJobs(grouped.ready),
      progress: filterJobs(grouped.progress),
      failedPickup: filterJobs(grouped.failedPickup ?? []),
      autoReview: autoReviewFiltered,
      humanReview: filterJobs(grouped.humanReview ?? []),
      review: autoReviewFiltered,
      completed: filterJobs(grouped.completed),
      archive: filterJobs(grouped.archive ?? []),
    } as GroupedJobs;
  });

  readonly filteredJobCount = computed(() => {
    const g = this.filteredGrouped();
    return (
      (g.preparation?.length ?? 0)
      + (g.orchestratorPrep?.length ?? 0)
      + (g.needsHumanReview?.length ?? 0)
      + (g.backlog?.length ?? 0)
      + (g.ready?.length ?? 0)
      + (g.progress?.length ?? 0)
      + (g.failedPickup?.length ?? 0)
      + (g.autoReview?.length ?? 0)
      + (g.humanReview?.length ?? 0)
      + (g.completed?.length ?? 0)
      + (g.archive?.length ?? 0)
    );
  });

  readonly totalJobCount = computed(() => {
    const g = this.jobService.grouped();
    return (
      (g.preparation?.length ?? 0)
      + (g.orchestratorPrep?.length ?? 0)
      + (g.needsHumanReview?.length ?? 0)
      + (g.backlog?.length ?? 0)
      + (g.ready?.length ?? 0)
      + (g.progress?.length ?? 0)
      + (g.failedPickup?.length ?? 0)
      + (g.autoReview?.length ?? (g.review?.length ?? 0))
      + (g.humanReview?.length ?? 0)
      + (g.completed?.length ?? 0)
      + (g.archive?.length ?? 0)
    );
  });

  // ---------- pill summary for the strip below the header ----------

  readonly activeFilterPills = computed<ActiveFilterPill[]>(() => {
    const pills: ActiveFilterPill[] = [];
    const owner = this.activeClientFilter();
    if (owner) {
      const c = this.clientService.resolve(owner);
      pills.push({
        kind: 'owner', kindLabel: 'Owner', value: owner,
        label: c.displayName || owner,
        swatch: c.colour || null,
      });
    }
    for (const name of this.activeProjects()) {
      const id = projectIdentity(name);
      pills.push({
        kind: 'project', kindLabel: 'Project', value: name,
        label: name, swatch: id.color,
      });
    }
    const t = this.activeType();
    if (t) {
      pills.push({
        kind: 'type', kindLabel: 'Type', value: t,
        label: typeFilterLabel(t),
        swatch: null,
      });
    }
    const byId = this.tagRegistryStore.byId();
    for (const id of this.activeTagFilter()) {
      const entry = byId.get(id);
      pills.push({
        kind: 'tag', kindLabel: 'Tag', value: id,
        label: entry?.label ?? id,
        swatch: entry?.color ?? null,
      });
    }
    return pills;
  });

  removeFilterPill(pill: ActiveFilterPill): void {
    switch (pill.kind) {
      case 'owner':
        this.setClientFilter(null);
        break;
      case 'project':
        this.toggleProject(pill.value);
        break;
      case 'type':
        this.onSetType(null);
        break;
      case 'tag':
        this.toggleTagFilter(pill.value);
        break;
    }
  }

  // ---------- mutators ----------

  setSearchQuery(value: string): void {
    this.searchQuery.set(value);
    this.writeFiltersToQueryParams();
  }

  clearSearch(): void {
    this.searchQuery.set('');
    this.writeFiltersToQueryParams();
  }

  setClientFilter(id: string | null): void {
    this.activeClientFilter.set(id || null);
    this.writeFilterHash();
  }

  /** Read the new value out of the (change) event so the template stays terse. */
  clientFilterChange(event: Event): string | null {
    const target = event.target as HTMLSelectElement | null;
    const v = target?.value ?? '';
    return v ? v : null;
  }

  clearTypeFilters(): void {
    this.activeTypeFilter.set(new Set());
    this.writeFilterHash();
  }

  /** Single-select set of the type filter (called from the dropdown). */
  onSetType(type: string | null): void {
    this.activeTypeFilter.set(type ? new Set([type]) : new Set());
    this.writeFilterHash();
  }

  toggleTypeFilter(type: string): void {
    const current = this.activeType();
    this.onSetType(current === type ? null : type);
  }

  toggleTagFilter(id: string): void {
    const next = new Set(this.activeTagFilter());
    if (next.has(id)) next.delete(id); else next.add(id);
    this.activeTagFilter.set(next);
    this.writeFilterHash();
  }

  toggleProject(name: string): void {
    const current = new Set(this.activeProjects());
    if (current.has(name)) current.delete(name); else current.add(name);
    this.activeProjects.set(current);
    localStorage.setItem('activeProjects', JSON.stringify([...current]));
    this.writeFilterHash();
  }

  /**
   * Idempotent single-project set. Used by reactive tab-sync effects where
   * repeated invocations with the same name must not toggle the filter off.
   */
  setSoleProject(name: string): void {
    const current = this.activeProjects();
    if (current.size === 1 && current.has(name)) return;
    this.activeProjects.set(new Set([name]));
    localStorage.setItem('activeProjects', JSON.stringify([name]));
    this.writeFilterHash();
  }

  /**
   * Single-select project switch. Clicking an inactive project chip with
   * no modifier replaces the active set with just that project, so the
   * board, lane counters, and chip strips switch cleanly between projects
   * instead of stacking filters. Clicking the only-active chip clears the
   * filter (back to "all projects"). For additive multi-select callers
   * pass `additive = true` (Ctrl/Cmd+click); that delegates to the legacy
   * toggle behaviour so power users can still compare two boards.
   */
  selectProject(name: string, additive: boolean): void {
    if (additive) {
      this.toggleProject(name);
      return;
    }
    const current = this.activeProjects();
    const isSoleActive = current.size === 1 && current.has(name);
    const next = isSoleActive ? new Set<string>() : new Set<string>([name]);
    this.activeProjects.set(next);
    localStorage.setItem('activeProjects', JSON.stringify([...next]));
    this.writeFilterHash();
  }

  isProjectActive(name: string): boolean {
    return this.activeProjects().has(name);
  }

  clearAllFilters(): void {
    this.activeTypeFilter.set(new Set());
    this.activeTagFilter.set(new Set());
    this.activeClientFilter.set(null);
    this.activeProjects.set(new Set());
    localStorage.setItem('activeProjects', '[]');
    this.writeFilterHash();
  }

  /**
   * Clears the search query in addition to all filters. Wired to the
   * sidesheet's "Clear all" affordance — the on-board "Clear all"
   * button only resets faceted filters, not the search box.
   */
  clearSearchAndFilters(): void {
    this.searchQuery.set('');
    this.clearAllFilters();
  }

  /**
   * Drop project names from the active filter that no longer exist in the
   * registry. Called once after watch-path data arrives so stale
   * localStorage values from before a registry rename don't leave the
   * board empty.
   */
  purgeStaleProjects(validNames: ReadonlySet<string>): void {
    const current = this.activeProjects();
    if (current.size === 0) return;
    const cleaned = new Set([...current].filter(n => validNames.has(n)));
    if (cleaned.size !== current.size) {
      this.activeProjects.set(cleaned);
      localStorage.setItem('activeProjects', JSON.stringify([...cleaned]));
      this.writeFilterHash();
    }
  }

  // ---------- URL sync ----------

  /**
   * Hydrate from URL hash + query params. Call once on app boot before
   * the board renders so a bookmark or copy-pasted address lands on
   * the same view.
   */
  hydrateFromUrl(): void {
    this.readFilterHash();
    this.readFiltersFromQueryParams();
  }

  private readFilterHash(): void {
    const hash = window.location.hash || '';
    const newM = hash.match(/[#&]filters=([^&]*)/);
    if (newM) {
      const decoded = decodeURIComponent(newM[1]);
      const parts = decoded.split(';').map(p => p.trim()).filter(Boolean);
      let owner: string | null = null;
      const projects = new Set<string>();
      const types = new Set<string>();
      const tags = new Set<string>();
      for (const p of parts) {
        const idx = p.indexOf(':');
        if (idx <= 0) continue;
        const k = p.slice(0, idx).trim();
        const v = p.slice(idx + 1).trim();
        if (!v) continue;
        if (k === 'owner') owner = v;
        else if (k === 'projects') v.split(',').filter(Boolean).forEach(x => projects.add(x));
        else if (k === 'type') types.add(v);
        else if (k === 'tags') v.split(',').filter(Boolean).forEach(x => tags.add(x));
      }
      this.activeClientFilter.set(owner);
      this.activeProjects.set(projects);
      localStorage.setItem('activeProjects', JSON.stringify([...projects]));
      const oneType = types.size > 0 ? new Set([types.values().next().value as string]) : new Set<string>();
      this.activeTypeFilter.set(oneType);
      this.activeTagFilter.set(tags);
      return;
    }
    const legacyM = hash.match(/[#&]filter=([^&]*)/);
    if (legacyM) {
      const parts = decodeURIComponent(legacyM[1]).split(',').map(p => p.trim()).filter(Boolean);
      const types = new Set<string>();
      const tags = new Set<string>();
      for (const p of parts) {
        const [k, v] = p.split(':');
        if (!k || !v) continue;
        if (k === 'type') types.add(v);
        else if (k === 'tag') tags.add(v);
      }
      const oneType = types.size > 0 ? new Set([types.values().next().value as string]) : new Set<string>();
      this.activeTypeFilter.set(oneType);
      this.activeTagFilter.set(tags);
    }
  }

  private writeFilterHash(): void {
    const segments: string[] = [];
    const owner = this.activeClientFilter();
    if (owner) segments.push(`owner:${owner}`);
    const projects = [...this.activeProjects()];
    if (projects.length > 0) segments.push(`projects:${projects.join(',')}`);
    const t = this.activeType();
    if (t) segments.push(`type:${t}`);
    const tags = [...this.activeTagFilter()];
    if (tags.length > 0) segments.push(`tags:${tags.join(',')}`);
    const fragment = segments.length > 0 ? `filters=${encodeURIComponent(segments.join(';'))}` : '';
    const existing = (window.location.hash || '').replace(/^#/, '');
    const others = existing
      .split('&')
      .filter(s => s && !s.startsWith('filters=') && !s.startsWith('filter='));
    const next = [fragment, ...others].filter(Boolean).join('&');
    const target = next ? `#${next}` : '';
    if (target !== window.location.hash) {
      history.replaceState(null, '', target || window.location.pathname + window.location.search);
    }
    this.writeFiltersToQueryParams();
  }

  private readFiltersFromQueryParams(): void {
    if (typeof window === 'undefined') return;
    const params = new URLSearchParams(window.location.search);
    const q = params.get('q');
    if (q != null) this.searchQuery.set(q);
    const owner = params.get('owner');
    if (owner) this.activeClientFilter.set(owner);
    const tagsCsv = params.get('tag');
    if (tagsCsv) {
      const tags = new Set(tagsCsv.split(',').filter(Boolean));
      if (tags.size > 0) this.activeTagFilter.set(tags);
    }
    const type = params.get('type');
    if (type) this.activeTypeFilter.set(new Set([type]));
  }

  private writeFiltersToQueryParams(): void {
    if (typeof window === 'undefined') return;
    const params = new URLSearchParams(window.location.search);
    const q = this.searchQuery();
    if (q) params.set('q', q); else params.delete('q');
    const owner = this.activeClientFilter();
    if (owner) params.set('owner', owner); else params.delete('owner');
    const tags = [...this.activeTagFilter()];
    if (tags.length > 0) params.set('tag', tags.join(',')); else params.delete('tag');
    const type = this.activeType();
    if (type) params.set('type', type); else params.delete('type');
    const qs = params.toString();
    const target =
      window.location.pathname
      + (qs ? `?${qs}` : '')
      + (window.location.hash || '');
    if (target !== window.location.pathname + window.location.search + (window.location.hash || '')) {
      history.replaceState(null, '', target);
    }
  }
}

function safeParseStringArray(raw: string | null): string[] {
  if (!raw) return [];
  try {
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.filter((s): s is string => typeof s === 'string') : [];
  } catch {
    return [];
  }
}

function typeFilterLabel(value: string): string {
  switch (value) {
    case 'bug': return '🐞 Bugs';
    case 'feature': return '✨ Features';
    case 'chore': return '· Chores';
    default: return value;
  }
}
