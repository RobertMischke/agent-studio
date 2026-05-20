import { Injectable, signal } from '@angular/core';

/**
 * Persists the orchestrator side-sheet panel width across reloads. Parallel
 * to {@link StudioPanelStateService} which does the same for the left
 * sidebar.
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

  readonly width = this._width.asReadonly();

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
}
