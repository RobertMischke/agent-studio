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
 */
@Injectable({ providedIn: 'root' })
export class UiPreferencesService {
  readonly taskNavCollapsed = signal<boolean>(localStorage.getItem('taskNavCollapsed') === '1');
  readonly compactCards = signal<boolean>(localStorage.getItem('compactCards') === '1');
  readonly sideSheetWidth = signal<number>(parseInt(localStorage.getItem('sideSheetWidth') ?? '280'));

  private resizing = false;

  setTaskNavCollapsed(collapsed: boolean): void {
    this.taskNavCollapsed.set(collapsed);
    localStorage.setItem('taskNavCollapsed', collapsed ? '1' : '0');
  }

  toggleCompactCards(): void {
    const next = !this.compactCards();
    this.compactCards.set(next);
    localStorage.setItem('compactCards', next ? '1' : '0');
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
      localStorage.setItem('sideSheetWidth', this.sideSheetWidth().toString());
      document.removeEventListener('mousemove', onMouseMove);
      document.removeEventListener('mouseup', onMouseUp);
    };

    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
  }
}
