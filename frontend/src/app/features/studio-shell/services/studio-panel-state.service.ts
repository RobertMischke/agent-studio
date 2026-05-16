import { Injectable, signal } from '@angular/core';
import type { StudioPanelKind } from '../studio-shell.types';

/**
 * Tracks which sidebar panel (Explorer, Tasks, Filters, …) is mounted and
 * whether the sidebar is visible at all. Clicking the same ActivityBar
 * icon a second time collapses the sidebar (VS-Code behaviour), so this
 * service holds both pieces of state and exposes one helper for the
 * toggling rule.
 */
@Injectable({ providedIn: 'root' })
export class StudioPanelStateService {
  private readonly _active = signal<StudioPanelKind>('explorer');
  private readonly _visible = signal<boolean>(true);
  private readonly _sidebarWidth = signal<number>(280);

  readonly active = this._active.asReadonly();
  readonly visible = this._visible.asReadonly();
  readonly sidebarWidth = this._sidebarWidth.asReadonly();

  /**
   * VS-Code-style toggle: clicking the active icon hides the sidebar;
   * clicking a different icon switches panel and ensures the sidebar is
   * visible. Returns the resulting visibility for callers that want to
   * react (e.g. status indicator).
   */
  toggle(panel: StudioPanelKind): boolean {
    if (this._active() === panel && this._visible()) {
      this._visible.set(false);
      return false;
    }
    this._active.set(panel);
    this._visible.set(true);
    return true;
  }

  setVisible(visible: boolean): void {
    this._visible.set(visible);
  }

  setSidebarWidth(width: number): void {
    this._sidebarWidth.set(Math.max(200, Math.min(560, Math.round(width))));
  }
}
