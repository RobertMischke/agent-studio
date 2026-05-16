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
  private readonly _sidebarWidth = signal<number>(
    parseInt(localStorage.getItem('atp.studio.sidebarWidth') ?? '280', 10) || 280,
  );
  private readonly _activityBarSide = signal<'left' | 'right'>(
    (localStorage.getItem('atp.studio.activityBarSide') as 'left' | 'right' | null) ?? 'left',
  );
  private readonly _chatRailOpen = signal<boolean>(
    localStorage.getItem('atp.studio.chatRailOpen') === '1',
  );

  readonly active = this._active.asReadonly();
  readonly visible = this._visible.asReadonly();
  readonly sidebarWidth = this._sidebarWidth.asReadonly();
  readonly activityBarSide = this._activityBarSide.asReadonly();
  readonly chatRailOpen = this._chatRailOpen.asReadonly();

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
    const clamped = Math.max(200, Math.min(560, Math.round(width)));
    this._sidebarWidth.set(clamped);
    localStorage.setItem('atp.studio.sidebarWidth', String(clamped));
  }

  setActivityBarSide(side: 'left' | 'right'): void {
    this._activityBarSide.set(side);
    localStorage.setItem('atp.studio.activityBarSide', side);
  }

  toggleChatRail(): void {
    const next = !this._chatRailOpen();
    this._chatRailOpen.set(next);
    localStorage.setItem('atp.studio.chatRailOpen', next ? '1' : '0');
  }

  setChatRailOpen(open: boolean): void {
    this._chatRailOpen.set(open);
    localStorage.setItem('atp.studio.chatRailOpen', open ? '1' : '0');
  }
}
