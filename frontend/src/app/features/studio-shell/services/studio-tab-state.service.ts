// Sticky default board tab — see ensureStickyDefault().
import { Injectable, computed, signal } from '@angular/core';
import type { StudioTab, BoardTab } from '../studio-shell.types';
import { studioTabKey } from '../studio-shell.types';

const STORAGE_KEY = 'atp.studio.tabs.v1';
const STORAGE_VERSION = 1;

/**
 * Always-on default board tab. Mounted at boot, restored on reload, and
 * preserved through every close-style op so the editor area can never
 * fall into a navigation dead-end. Default is the cross-project board
 * (`__all__`). A follow-up may let the user choose between cross-project
 * and last-active project; the navigation-deadend fix only needs the
 * default present.
 */
const STICKY_DEFAULT: BoardTab = { kind: 'board', projectName: '__all__', sticky: true };

interface PersistedState {
  v: number;
  tabs: StudioTab[];
  activeKey: string | null;
}

/**
 * Owns the open-editor-tab list and the active tab for the studio shell.
 *
 * Behaviour mirrors the VS-Code editor surface: opening a tab that is
 * already present focuses it instead of duplicating; closing the active
 * tab falls back to the last remaining tab (or the welcome screen when
 * the list goes empty). Drag-reorder is supported through {@link move}.
 *
 * State is persisted to <code>localStorage</code> under the versioned
 * key {@link STORAGE_KEY} so the editor surface looks the same after a
 * reload. The version prefix lets us evolve the tab shape without
 * silently breaking older snapshots; a payload with an unknown version
 * is dropped rather than crashing the boot.
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

  constructor() {
    this.restore();
    this.ensureStickyDefault();
  }

  /** True when the tab keyed by `key` is the sticky default board tab. */
  isStickyKey(key: string): boolean {
    return this._tabs().some(t => studioTabKey(t) === key && this.isSticky(t));
  }

  /** Resolve the sticky tab's key, or null if none is mounted yet. */
  stickyKey(): string | null {
    const sticky = this._tabs().find(t => this.isSticky(t));
    return sticky ? studioTabKey(sticky) : null;
  }

  /**
   * Focus the sticky default board tab; called from the activity-bar
   * Board button and the Ctrl+B shortcut. Returns the activated key, or
   * null if no sticky tab is mounted (shouldn't happen — `ensureStickyDefault`
   * runs at construction and after restore).
   */
  activateSticky(): string | null {
    const key = this.stickyKey();
    if (!key) return null;
    this._activeKey.set(key);
    this.persist();
    return key;
  }

  private isSticky(tab: StudioTab): boolean {
    return tab.kind === 'board' && !!tab.sticky;
  }

  /**
   * Guarantee the sticky default board tab is present in the list. Called
   * once at construction (after `restore()`). If a non-sticky board tab
   * for the same project name already exists, promote it instead of
   * inserting a duplicate. If no active key is set, activate the sticky
   * tab so the editor surface is never blank.
   */
  private ensureStickyDefault(): void {
    const tabs = this._tabs();
    if (tabs.some(t => this.isSticky(t))) {
      // Existing sticky → only re-anchor the active key when the persisted
      // value is gone (e.g. corrupted payload restored without an activeKey).
      if (this._activeKey() === null) this._activeKey.set(this.stickyKey());
      return;
    }
    const existingIdx = tabs.findIndex(
      t => t.kind === 'board' && t.projectName === STICKY_DEFAULT.projectName,
    );
    if (existingIdx >= 0) {
      const next = tabs.slice();
      next[existingIdx] = { ...(next[existingIdx] as BoardTab), sticky: true };
      this._tabs.set(next);
    } else {
      this._tabs.set([STICKY_DEFAULT, ...tabs]);
    }
    if (this._activeKey() === null) {
      this._activeKey.set(studioTabKey(STICKY_DEFAULT));
    }
    this.persist();
  }

  /** Open the tab if it's not already there, then focus it. */
  open(tab: StudioTab): void {
    const key = studioTabKey(tab);
    this._tabs.update(list => list.some(t => studioTabKey(t) === key) ? list : [...list, tab]);
    this._activeKey.set(key);
    this.persist();
  }

  /** Focus an existing tab by key. No-op when the key is unknown. */
  select(key: string): void {
    if (this._tabs().some(t => studioTabKey(t) === key)) {
      this._activeKey.set(key);
      this.persist();
    }
  }

  /**
   * Close the tab; if it was active, fall back to the last remaining tab.
   * Sticky tabs (`isSticky`) are preserved — calling close on the sticky
   * key is a no-op so the editor area never empties.
   */
  close(key: string): void {
    if (this.isStickyKey(key)) return;
    const list = this._tabs();
    const next = list.filter(t => studioTabKey(t) !== key);
    this._tabs.set(next);
    if (this._activeKey() === key) {
      this._activeKey.set(next.length ? studioTabKey(next[next.length - 1]) : null);
    }
    this.persist();
  }

  /**
   * Close every tab except the one given. Sticky tabs are preserved in
   * addition to the named one.
   */
  closeOthers(keepKey: string): void {
    const list = this._tabs();
    const next = list.filter(t => studioTabKey(t) === keepKey || this.isSticky(t));
    this._tabs.set(next);
    if (next.some(t => studioTabKey(t) === keepKey)) {
      this._activeKey.set(keepKey);
    } else if (next.length) {
      this._activeKey.set(studioTabKey(next[next.length - 1]));
    } else {
      this._activeKey.set(null);
    }
    this.persist();
  }

  /**
   * Close every tab whose index is strictly greater than the anchor's.
   * Sticky tabs are preserved even when they sit past the anchor.
   */
  closeRight(anchorKey: string): void {
    const list = this._tabs();
    const idx = list.findIndex(t => studioTabKey(t) === anchorKey);
    if (idx < 0) return;
    const next = list.filter((t, i) => i <= idx || this.isSticky(t));
    this._tabs.set(next);
    if (!next.some(t => studioTabKey(t) === this._activeKey())) {
      this._activeKey.set(anchorKey);
    }
    this.persist();
  }

  /**
   * Close every tab whose index is strictly less than the anchor's.
   * Sticky tabs are preserved even when they sit before the anchor.
   */
  closeLeft(anchorKey: string): void {
    const list = this._tabs();
    const idx = list.findIndex(t => studioTabKey(t) === anchorKey);
    if (idx < 0) return;
    const next = list.filter((t, i) => i >= idx || this.isSticky(t));
    this._tabs.set(next);
    if (!next.some(t => studioTabKey(t) === this._activeKey())) {
      this._activeKey.set(anchorKey);
    }
    this.persist();
  }

  /** Close every tab. Sticky tabs are preserved and one becomes active. */
  closeAll(): void {
    const sticky = this._tabs().filter(t => this.isSticky(t));
    this._tabs.set(sticky);
    this._activeKey.set(sticky.length ? studioTabKey(sticky[0]) : null);
    this.persist();
  }

  /**
   * Reorder: move the tab with <code>sourceKey</code> so it lands in the
   * slot <em>before</em> the tab with <code>targetKey</code>. Drop on the
   * source itself is a no-op. To move a tab to the very end, pass
   * <code>targetKey = null</code>.
   *
   * The active tab key never changes — moving the active tab keeps it
   * focused; moving a non-active tab leaves the focus where it was.
   */
  move(sourceKey: string, targetKey: string | null): void {
    const list = this._tabs();
    const fromIdx = list.findIndex(t => studioTabKey(t) === sourceKey);
    if (fromIdx < 0) return;
    if (sourceKey === targetKey) return;

    const next = list.slice();
    const [moved] = next.splice(fromIdx, 1);

    if (targetKey === null) {
      next.push(moved);
    } else {
      // After removal the target's index may have shifted left by 1.
      const targetIdx = next.findIndex(t => studioTabKey(t) === targetKey);
      if (targetIdx < 0) {
        // Target vanished mid-flight (rare race). Put it back where it was.
        next.splice(fromIdx, 0, moved);
        return;
      }
      next.splice(targetIdx, 0, moved);
    }
    this._tabs.set(next);
    this.persist();
  }

  /**
   * Remove board and hub tabs whose projectName is no longer in the
   * registry. Called once after the watch-path data arrives so tabs
   * persisted under a pre-rename name don't produce empty boards.
   */
  purgeStaleProjectTabs(validNames: ReadonlySet<string>): void {
    const before = this._tabs();
    const after = before.filter(t => {
      // Sticky tabs are immune — the navigation fallback must always exist.
      if (this.isSticky(t)) return true;
      if (t.kind === 'board' && t.projectName !== '__all__') return validNames.has(t.projectName);
      if (t.kind === 'hub') return validNames.has(t.projectName);
      return true;
    });
    if (after.length === before.length) return;
    this._tabs.set(after);
    if (!after.some(t => studioTabKey(t) === this._activeKey())) {
      this._activeKey.set(after.length ? studioTabKey(after[after.length - 1]) : null);
    }
    this.persist();
  }

  // ---- persistence ----------------------------------------------------

  private restore(): void {
    if (typeof window === 'undefined') return;
    try {
      const raw = window.localStorage?.getItem(STORAGE_KEY);
      if (!raw) return;
      const parsed = JSON.parse(raw) as PersistedState;
      if (!parsed || parsed.v !== STORAGE_VERSION) return;
      if (!Array.isArray(parsed.tabs)) return;
      // Drop any tab entries that don't round-trip through studioTabKey;
      // a future StudioTabKind variant the running build doesn't recognise
      // would otherwise live as a ghost row in the tab bar.
      const safeTabs = parsed.tabs.filter(t => {
        try { return typeof studioTabKey(t) === 'string'; }
        catch { return false; }
      });
      this._tabs.set(safeTabs);
      // Only restore the active key if it points at a surviving tab.
      const validKey = safeTabs.some(t => studioTabKey(t) === parsed.activeKey);
      this._activeKey.set(validKey ? parsed.activeKey : (safeTabs.length ? studioTabKey(safeTabs[safeTabs.length - 1]) : null));
    } catch {
      // Corrupt payload — drop it. The effect will overwrite next write.
    }
  }

  private persist(): void {
    if (typeof window === 'undefined') return;
    try {
      const payload: PersistedState = {
        v: STORAGE_VERSION,
        tabs: this._tabs(),
        activeKey: this._activeKey(),
      };
      window.localStorage?.setItem(STORAGE_KEY, JSON.stringify(payload));
    } catch {
      /* storage may be full / blocked; signals still reflect live state */
    }
  }
}
