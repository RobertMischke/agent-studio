import { Injectable, effect, signal } from '@angular/core';

const THEME_KEY = 'atp.studio.theme';

/**
 * AGT-2035 — single source of truth for the app theme.
 *
 * Theme used to be a local signal on `StudioShellComponent`; the settings
 * consolidation makes Theme a *global* preference surfaced in the Appearance
 * section of the one Workspace-settings view, while the titlebar keeps its
 * moon/sun quick-toggle. Both now read and write this one service instead of
 * each owning a copy, so they can never drift.
 *
 * The service reflects the current theme onto `document.documentElement`
 * (`data-studio-theme`) so the design tokens flip, and persists the choice to
 * localStorage. Default is Light — a missing key counts as "use the default".
 */
@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly _theme = signal<'dark' | 'light'>(
    (localStorage.getItem(THEME_KEY) as 'dark' | 'light' | null) ?? 'light',
  );

  /** Read-only current theme. */
  readonly theme = this._theme.asReadonly();

  constructor() {
    // Reflect the theme onto the document root + persist on every change.
    effect(() => {
      const t = this._theme();
      document.documentElement.dataset['studioTheme'] = t;
      try { localStorage.setItem(THEME_KEY, t); } catch { /* storage blocked */ }
    });
  }

  set(value: 'dark' | 'light'): void {
    this._theme.set(value);
  }

  toggle(): void {
    this._theme.update(t => (t === 'dark' ? 'light' : 'dark'));
  }
}
