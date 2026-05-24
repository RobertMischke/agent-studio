import { Injectable, signal } from '@angular/core';

/**
 * Cycle 9 shell feature service: durable user-interface preferences
 * that survive across reloads. Lifted out of `app.ts` per ADR-0034 so
 * the shell stops owning persistence wiring for prefs that have
 * nothing to do with kanban data flow.
 *
 * State backed by localStorage today (the keys match the pre-extraction
 * shell exactly, so the move is invisible to existing users):
 *
 *   - `taskNavCollapsed`  - left task-nav collapsed or expanded
 *   - `compactCards`      - dense vs full job-card mode
 *   - `sideSheetWidth`    - resizable side-sheet width in pixels
 *
 * Methods are deliberately small: no derived state, no computeds, no
 * effects. The service is a thin wrapper around three signals + their
 * persistence so its surface area stays trivial to read.
 *
 * `startResize` lives here because the drag handlers it installs
 * directly call `sideSheetWidth.set` on every mousemove and persist
 * on mouseup; keeping that flow inside the service avoids leaking
 * "I am editing the side-sheet width right now" state into the shell.
 *
 * F24: a `storage` listener mirrors writes from sibling browser tabs
 * back into the local signals so two tabs of the same app converge on
 * a shared UI posture within a single event loop. localStorage is
 * already the persistence channel; the `storage` event is the only
 * way one tab finds out the other one wrote.
 */
const STORAGE_KEY_TASK_NAV = 'taskNavCollapsed';
const STORAGE_KEY_COMPACT_CARDS = 'compactCards';
const STORAGE_KEY_SIDE_SHEET_WIDTH = 'sideSheetWidth';

@Injectable({ providedIn: 'root' })
export class UiPreferencesService {
  readonly taskNavCollapsed = signal<boolean>(localStorage.getItem(STORAGE_KEY_TASK_NAV) === '1');
  readonly compactCards = signal<boolean>(localStorage.getItem(STORAGE_KEY_COMPACT_CARDS) === '1');
  readonly sideSheetWidth = signal<number>(
    parseInt(localStorage.getItem(STORAGE_KEY_SIDE_SHEET_WIDTH) ?? '280'),
  );

  /**
   * F43: per-tab override that beats the rail-open auto-compact rule
   * inside `effectiveCompactCards` (lives in `app.ts`). Set when the
   * user explicitly toggles to "Full" while the orchestrator rail is
   * open; cleared the next time the rail closes (handled by the shell).
   * Not persisted: a fresh tab/reload should re-engage the rail rule.
   * Not cross-tab synced (would surprise the second tab whose rail is
   * still closed).
   */
  readonly userOverridesCompactWhileRail = signal<boolean>(false);

  private resizing = false;

  constructor() {
    if (typeof window !== 'undefined') {
      window.addEventListener('storage', this.onStorageEvent);
    }
  }

  /**
   * F24: cross-tab sync. The `storage` event only fires in OTHER tabs
   * when localStorage changes - never in the tab that did the write -
   * so we can mirror unconditionally without re-entry. We tolerate
   * `null` newValue (key cleared) by falling back to the documented
   * default; corrupt int parses fall back to the same default the
   * constructor used.
   */
  private readonly onStorageEvent = (e: StorageEvent): void => {
    if (e.storageArea !== null && e.storageArea !== localStorage) return;
    switch (e.key) {
      case STORAGE_KEY_TASK_NAV:
        this.taskNavCollapsed.set(e.newValue === '1');
        return;
      case STORAGE_KEY_COMPACT_CARDS:
        this.compactCards.set(e.newValue === '1');
        return;
      case STORAGE_KEY_SIDE_SHEET_WIDTH: {
        const parsed = parseInt(e.newValue ?? '280');
        this.sideSheetWidth.set(Number.isFinite(parsed) ? parsed : 280);
        return;
      }
      default:
        return;
    }
  };

  setTaskNavCollapsed(collapsed: boolean): void {
    this.taskNavCollapsed.set(collapsed);
    localStorage.setItem(STORAGE_KEY_TASK_NAV, collapsed ? '1' : '0');
  }

  toggleCompactCards(): void {
    this.setCompactCards(!this.compactCards());
  }

  /**
   * F43: explicit setter so callers that already know the intended
   * next value (e.g. the shell's rail-aware toggle that has to set the
   * pref to the user's intended effective state, not just flip the
   * current pref) don't have to read-then-toggle.
   */
  setCompactCards(value: boolean): void {
    this.compactCards.set(value);
    localStorage.setItem(STORAGE_KEY_COMPACT_CARDS, value ? '1' : '0');
  }

  /**
   * Side-sheet drag handler. Adds a `body.resizing` class while drag
   * is active so the cursor stays as the resize affordance even when
   * the pointer leaves the handle. Min width 200 px; no max - the
   * sheet can grow as wide as the viewport allows.
   */
  startResize(event: MouseEvent): void {
    event.preventDefault();
    this.resizing = true;
    document.body.classList.add('resizing');

    const startX = event.clientX;
    const startWidth = this.sideSheetWidth();

    const onMouseMove = (e: MouseEvent) => {
      if (!this.resizing) return;
      const deltaX = e.clientX - startX;
      const newWidth = Math.max(200, startWidth + deltaX);
      this.sideSheetWidth.set(newWidth);
    };

    const onMouseUp = () => {
      this.resizing = false;
      document.body.classList.remove('resizing');
      localStorage.setItem(STORAGE_KEY_SIDE_SHEET_WIDTH, this.sideSheetWidth().toString());
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
    };

    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
  }
}
