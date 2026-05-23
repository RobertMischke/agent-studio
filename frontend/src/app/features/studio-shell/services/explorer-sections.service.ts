import { Injectable, signal } from '@angular/core';

/**
 * F27: per-section collapse state for the Explorer-tree folder headers
 * (Workspace, Open tabs). Keys are arbitrary strings so panels can opt
 * in by reading/writing their own key — no enum, no schema change when
 * a new folder header is migrated.
 *
 * Persists to `localStorage['atp.studio.explorerSections']` as a
 * `{ [key]: true }` object. Only `true` values are stored so the
 * default (everything expanded) maps to an empty record.
 */
const STORAGE_KEY = 'atp.studio.explorerSections';

@Injectable({ providedIn: 'root' })
export class ExplorerSectionsService {
  private readonly _state = signal<Map<string, boolean>>(read());
  readonly state = this._state.asReadonly();

  isCollapsed(key: string): boolean {
    return this._state().get(key) === true;
  }

  setCollapsed(key: string, collapsed: boolean): void {
    this._state.update(map => {
      const next = new Map(map);
      if (collapsed) next.set(key, true);
      else next.delete(key);
      write(next);
      return next;
    });
  }
}

function read(): Map<string, boolean> {
  const map = new Map<string, boolean>();
  if (typeof window === 'undefined') return map;
  try {
    const raw = window.localStorage?.getItem(STORAGE_KEY);
    if (!raw) return map;
    const obj = JSON.parse(raw) as unknown;
    if (obj && typeof obj === 'object' && !Array.isArray(obj)) {
      for (const [k, v] of Object.entries(obj as Record<string, unknown>)) {
        if (v === true) map.set(k, true);
      }
    }
  } catch {
    /* ignore */
  }
  return map;
}

function write(map: Map<string, boolean>): void {
  if (typeof window === 'undefined') return;
  try {
    const obj: Record<string, boolean> = {};
    for (const [k, v] of map) if (v) obj[k] = true;
    window.localStorage?.setItem(STORAGE_KEY, JSON.stringify(obj));
  } catch {
    /* storage may be full / blocked */
  }
}
