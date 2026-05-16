import { Injectable, computed, signal } from '@angular/core';
import type { StudioTab } from '../studio-shell.types';
import { studioTabKey } from '../studio-shell.types';

/**
 * Owns the open-editor-tab list and the active tab for the studio shell.
 *
 * Behaviour mirrors the VS-Code editor surface: opening a tab that is
 * already present focuses it instead of duplicating; closing the active
 * tab falls back to the last remaining tab (or the welcome screen when
 * the list goes empty). The state is in-memory only — persistence across
 * reloads is a follow-up slice and not required for the first cut.
 */
@Injectable({ providedIn: 'root' })
export class StudioTabStateService {
  private readonly _tabs = signal<StudioTab[]>([]);
  private readonly _activeKey = signal<string | null>(null);

  readonly tabs = this._tabs.asReadonly();
  readonly activeKey = this._activeKey.asReadonly();

  readonly activeTab = computed<StudioTab | null>(() => {
    const key = this._activeKey();
    if (!key) return null;
    return this._tabs().find(t => studioTabKey(t) === key) ?? null;
  });

  /** Open the tab if it's not already there, then focus it. */
  open(tab: StudioTab): void {
    const key = studioTabKey(tab);
    this._tabs.update(list => list.some(t => studioTabKey(t) === key) ? list : [...list, tab]);
    this._activeKey.set(key);
  }

  /** Focus an existing tab by key. No-op when the key is unknown. */
  select(key: string): void {
    if (this._tabs().some(t => studioTabKey(t) === key)) {
      this._activeKey.set(key);
    }
  }

  /** Close the tab; if it was active, fall back to the last remaining tab. */
  close(key: string): void {
    const list = this._tabs();
    const next = list.filter(t => studioTabKey(t) !== key);
    this._tabs.set(next);
    if (this._activeKey() === key) {
      this._activeKey.set(next.length ? studioTabKey(next[next.length - 1]) : null);
    }
  }

  /** Close every tab except the one given. */
  closeOthers(keepKey: string): void {
    const keep = this._tabs().find(t => studioTabKey(t) === keepKey);
    this._tabs.set(keep ? [keep] : []);
    this._activeKey.set(keep ? keepKey : null);
  }

  /** Close every tab whose index is strictly greater than the anchor's. */
  closeRight(anchorKey: string): void {
    const list = this._tabs();
    const idx = list.findIndex(t => studioTabKey(t) === anchorKey);
    if (idx < 0) return;
    const next = list.slice(0, idx + 1);
    this._tabs.set(next);
    if (!next.some(t => studioTabKey(t) === this._activeKey())) {
      this._activeKey.set(anchorKey);
    }
  }

  /** Close every tab whose index is strictly less than the anchor's. */
  closeLeft(anchorKey: string): void {
    const list = this._tabs();
    const idx = list.findIndex(t => studioTabKey(t) === anchorKey);
    if (idx < 0) return;
    const next = list.slice(idx);
    this._tabs.set(next);
    if (!next.some(t => studioTabKey(t) === this._activeKey())) {
      this._activeKey.set(anchorKey);
    }
  }

  /** Close every tab. */
  closeAll(): void {
    this._tabs.set([]);
    this._activeKey.set(null);
  }
}
