import { Injectable, signal } from '@angular/core';

const STORAGE_KEY = 'atp.studio.explorer.workbenchSections';

/**
 * Project-scoped disclosure state for the Explorer's Workbenches branches.
 *
 * A missing project is collapsed by default. Expanded projects are persisted
 * explicitly so reopening the Studio restores the operator's tree shape.
 */
@Injectable({ providedIn: 'root' })
export class ExplorerWorkbenchStateService {
  private readonly _expandedProjects = signal<ReadonlySet<string>>(read());
  readonly expandedProjects = this._expandedProjects.asReadonly();

  isExpanded(projectName: string): boolean {
    return this._expandedProjects().has(projectName);
  }

  setExpanded(projectName: string, expanded: boolean): void {
    const key = projectName.trim();
    if (!key || this.isExpanded(key) === expanded) return;

    this._expandedProjects.update(projects => {
      const next = new Set(projects);
      if (expanded) next.add(key);
      else next.delete(key);
      write(next);
      return next;
    });
  }
}

function read(): ReadonlySet<string> {
  if (typeof window === 'undefined') return new Set<string>();
  try {
    const raw = window.localStorage?.getItem(STORAGE_KEY);
    if (!raw) return new Set<string>();
    const value = JSON.parse(raw) as unknown;
    if (!Array.isArray(value)) return new Set<string>();
    return new Set(value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0));
  } catch {
    return new Set<string>();
  }
}

function write(projects: ReadonlySet<string>): void {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage?.setItem(STORAGE_KEY, JSON.stringify([...projects].sort()));
  } catch {
    /* storage may be full or blocked */
  }
}
