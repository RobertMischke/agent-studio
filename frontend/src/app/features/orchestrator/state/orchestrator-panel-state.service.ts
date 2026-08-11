import { Injectable, signal } from '@angular/core';

/**
 * Persists the orchestrator side-sheet posture. Open state is scoped to the
 * browser tab through sessionStorage; panel width remains a durable layout
 * preference in localStorage.
 *
 * Why a service rather than a component-local signal: the width is layout-
 * level state — the studio-shell's overall geometry has to know about it
 * (e.g. e2e tests that pin push contracts at specific viewport widths), so
 * we keep it in one DI-shared place instead of reaching into the side
 * sheet component instance.
 */
@Injectable({ providedIn: 'root' })
export class OrchestratorPanelStateService {
  private static readonly STORAGE_KEY = 'atp.studio.orchestratorWidth';
  private static readonly OPEN_STORAGE_KEY = 'atp.studio.orchestratorOpen.v1';
  // Defaults match the previous static `:host(.is-open) { width: min(640px,
  // 96vw) }` cap so existing screenshots / Playwright runs continue to
  // resolve to 640 px at common viewports.
  private static readonly DEFAULT = 640;
  // Floor: narrower than this and the sidesheet header (project combo +
  // close button) starts to wrap unpleasantly.
  private static readonly MIN = 360;
  // Ceiling: anything wider squeezes the editor pane uselessly thin even
  // at 1920 px viewports. Resize clamps to min(MAX_ABS, 96vw) at runtime.
  private static readonly MAX_ABS = 1100;

  private readonly _width = signal<number>(this.readInitial());
  private readonly _open = signal(this.readInitialOpen());
  private _hasPersistedOpenState = this.readPersistedOpenState() !== null;

  readonly width = this._width.asReadonly();
  readonly open = this._open.asReadonly();

  /** Whether this browser tab already has an explicit operator posture. */
  hasPersistedOpenState(): boolean {
    return this._hasPersistedOpenState;
  }

  setOpen(open: boolean): void {
    this._open.set(open);
    this._hasPersistedOpenState = true;
    try {
      sessionStorage.setItem(OrchestratorPanelStateService.OPEN_STORAGE_KEY, open ? '1' : '0');
    } catch {
      // Session storage is optional. The injected singleton still preserves
      // the posture across in-app navigation when storage is unavailable.
    }
  }

  setWidth(px: number): void {
    const clamped = this.clamp(px);
    this._width.set(clamped);
    try {
      localStorage.setItem(OrchestratorPanelStateService.STORAGE_KEY, String(clamped));
    } catch {
      // localStorage may throw in private mode or storage-full scenarios.
      // The in-memory signal still tracks the latest value for this session.
    }
  }

  /**
   * Clamp a candidate width to [MIN, min(MAX_ABS, 96vw)]. Exposed so the
   * resize-drag handler can pre-clamp the live drag value to drive a
   * visually accurate cursor without committing it to storage.
   */
  clamp(px: number): number {
    const vw = typeof window !== 'undefined' ? window.innerWidth : OrchestratorPanelStateService.MAX_ABS;
    const max = Math.min(OrchestratorPanelStateService.MAX_ABS, Math.floor(vw * 0.96));
    return Math.max(OrchestratorPanelStateService.MIN, Math.min(max, Math.round(px)));
  }

  private readInitial(): number {
    try {
      const raw = localStorage.getItem(OrchestratorPanelStateService.STORAGE_KEY);
      if (!raw) return OrchestratorPanelStateService.DEFAULT;
      const parsed = parseInt(raw, 10);
      if (!Number.isFinite(parsed)) return OrchestratorPanelStateService.DEFAULT;
      return this.clamp(parsed);
    } catch {
      return OrchestratorPanelStateService.DEFAULT;
    }
  }

  private readInitialOpen(): boolean {
    return this.readPersistedOpenState() ?? false;
  }

  private readPersistedOpenState(): boolean | null {
    try {
      const raw = sessionStorage.getItem(OrchestratorPanelStateService.OPEN_STORAGE_KEY);
      return raw === '1' ? true : raw === '0' ? false : null;
    } catch {
      return null;
    }
  }
}
