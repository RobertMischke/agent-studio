import { Injectable, signal } from '@angular/core';

export type PaneName = 'prompt' | 'protocol' | 'git';
export interface PanesVisible { prompt: boolean; protocol: boolean; git: boolean; }
export interface PaneWeights { prompt: number; protocol: number; git: number; }

const LS_VISIBLE = 'taskboard.panesVisible';
const LS_WEIGHTS = 'taskboard.paneWeights';
const LS_DETAIL_PCT = 'taskboard.detailPanePercent';

const DETAIL_PCT_MIN = 35;
const DETAIL_PCT_MAX = 72;
const DETAIL_PCT_DEFAULT = 54;

const VISIBLE_FALLBACK: PanesVisible = { prompt: true, protocol: true, git: false };
// Chat-first redesign: the protocol pane (right column) hosts the
// conversation the user actually reads, so the default split now
// gives it ~62 % of the horizontal space versus the prompt's ~38 %
// when only prompt + protocol are visible (the default visibility).
// The user's saved choice in localStorage still wins on subsequent
// visits — this only changes first-load behaviour for new browsers /
// freshly-cleared storage.
const WEIGHTS_FALLBACK: PaneWeights = { prompt: 3, protocol: 5, git: 4 };

/**
 * Owns the three-pane layout state of the job-detail view: which panes are
 * visible, their flex weights, the maximize-toggle, and the wider detail
 * column percentage. All persistence lives here so the component stays
 * concerned only with rendering.
 *
 * Provided locally on TaskDetailComponent (`providers: [LayoutPanesService]`)
 * so each detail instance gets its own state — but persistence is shared
 * across instances via localStorage, matching the previous behaviour.
 */
@Injectable()
export class LayoutPanesService {
  readonly panesVisible = signal<PanesVisible>(loadVisible());
  readonly paneWeights = signal<PaneWeights>(loadWeights());
  readonly maximizedPane = signal<PaneName | null>(null);
  readonly detailPanePercent = signal<number>(loadDetailPercent());
  // True while the user is dragging the prompt|protocol or
  // protocol|git pane splitter. Template binds the
  // `pane__splitter--dragging` modifier off this so the visible line
  // stays highlighted for the duration of the drag, not just on hover.
  readonly paneSplitterDragging = signal(false);

  private layoutResizeBounds: DOMRect | null = null;
  private readonly layoutResizeMove = (e: PointerEvent) => this.resizeLayout(e);
  private readonly layoutResizeEnd = () => this.stopLayoutResize();

  private paneResizeBounds: DOMRect | null = null;
  private paneResizeLeft: 'prompt' | 'protocol' | null = null;
  private paneResizeRight: 'protocol' | 'git' | null = null;
  private paneResizeStartTotal = 0;
  private readonly paneResizeMove = (e: PointerEvent) => this.resizePanes(e);
  private readonly paneResizeEnd = () => this.stopPaneResize();

  togglePane(name: PaneName): PanesVisible {
    const next = { ...this.panesVisible(), [name]: !this.panesVisible()[name] };
    this.panesVisible.set(next);
    safeWrite(LS_VISIBLE, JSON.stringify(next));
    if (this.maximizedPane() === name && !next[name]) this.maximizedPane.set(null);
    return next;
  }

  toggleMaximize(name: PaneName): void {
    this.maximizedPane.set(this.maximizedPane() === name ? null : name);
  }

  /** Whether a pane should currently be in the DOM. */
  isPaneRendered(name: PaneName): boolean {
    if (!this.panesVisible()[name]) return false;
    const max = this.maximizedPane();
    return !max || max === name;
  }

  /** First visible pane to the right of `name`, used to bind the splitter. */
  firstVisibleAfter(name: PaneName): 'protocol' | 'git' {
    if (name === 'prompt') return this.panesVisible().protocol ? 'protocol' : 'git';
    return 'git';
  }

  // === Detail column resize (board / detail split) =======================

  startLayoutResize(event: PointerEvent): void {
    event.preventDefault();
    const layout = (event.currentTarget as HTMLElement).parentElement;
    if (!layout) return;
    this.layoutResizeBounds = layout.getBoundingClientRect();
    window.addEventListener('pointermove', this.layoutResizeMove);
    window.addEventListener('pointerup', this.layoutResizeEnd, { once: true });
  }

  private resizeLayout(event: PointerEvent): void {
    if (!this.layoutResizeBounds) return;
    const raw = ((event.clientX - this.layoutResizeBounds.left) / this.layoutResizeBounds.width) * 100;
    const clamped = Math.max(DETAIL_PCT_MIN, Math.min(DETAIL_PCT_MAX, raw));
    this.detailPanePercent.set(Math.round(clamped * 10) / 10);
  }

  /**
   * Public so TaskDetailComponent.ngOnDestroy can ensure listeners get
   * cleaned up if the component is torn down mid-drag.
   */
  stopLayoutResize(): void {
    window.removeEventListener('pointermove', this.layoutResizeMove);
    window.removeEventListener('pointerup', this.layoutResizeEnd);
    if (this.layoutResizeBounds) {
      this.layoutResizeBounds = null;
      safeWrite(LS_DETAIL_PCT, String(this.detailPanePercent()));
    }
  }

  // === Pane splitter resize ==============================================

  startPaneResize(event: PointerEvent, left: 'prompt' | 'protocol', right: 'protocol' | 'git'): void {
    event.preventDefault();
    const container = (event.currentTarget as HTMLElement).parentElement;
    if (!container) return;
    this.paneResizeBounds = container.getBoundingClientRect();
    this.paneResizeLeft = left;
    this.paneResizeRight = right;
    this.paneResizeStartTotal = this.paneWeights()[left] + this.paneWeights()[right];
    this.paneSplitterDragging.set(true);
    window.addEventListener('pointermove', this.paneResizeMove);
    window.addEventListener('pointerup', this.paneResizeEnd, { once: true });
  }

  private resizePanes(event: PointerEvent): void {
    if (!this.paneResizeBounds || !this.paneResizeLeft || !this.paneResizeRight) return;
    const { left, width } = this.paneResizeBounds;
    const ratio = Math.max(0.1, Math.min(0.9, (event.clientX - left) / width));
    const total = this.paneResizeStartTotal;
    const w = { ...this.paneWeights() };
    w[this.paneResizeLeft]  = Math.max(0.5, total * ratio);
    w[this.paneResizeRight] = Math.max(0.5, total * (1 - ratio));
    this.paneWeights.set(w);
  }

  private stopPaneResize(): void {
    window.removeEventListener('pointermove', this.paneResizeMove);
    window.removeEventListener('pointerup', this.paneResizeEnd);
    this.paneResizeBounds = null;
    this.paneResizeLeft = null;
    this.paneResizeRight = null;
    this.paneSplitterDragging.set(false);
    safeWrite(LS_WEIGHTS, JSON.stringify(this.paneWeights()));
  }
}

function loadVisible(): PanesVisible {
  try {
    const raw = localStorage.getItem(LS_VISIBLE);
    if (!raw) return VISIBLE_FALLBACK;
    const parsed = JSON.parse(raw);
    return {
      prompt:   typeof parsed.prompt   === 'boolean' ? parsed.prompt   : VISIBLE_FALLBACK.prompt,
      protocol: typeof parsed.protocol === 'boolean' ? parsed.protocol : VISIBLE_FALLBACK.protocol,
      git:      typeof parsed.git      === 'boolean' ? parsed.git      : VISIBLE_FALLBACK.git
    };
  } catch { return VISIBLE_FALLBACK; }
}

function loadWeights(): PaneWeights {
  try {
    const raw = localStorage.getItem(LS_WEIGHTS);
    if (!raw) return WEIGHTS_FALLBACK;
    const parsed = JSON.parse(raw);
    const norm = (v: unknown, f: number) => typeof v === 'number' && v > 0 && Number.isFinite(v) ? v : f;
    return {
      prompt:   norm(parsed.prompt,   WEIGHTS_FALLBACK.prompt),
      protocol: norm(parsed.protocol, WEIGHTS_FALLBACK.protocol),
      git:      norm(parsed.git,      WEIGHTS_FALLBACK.git)
    };
  } catch { return WEIGHTS_FALLBACK; }
}

function loadDetailPercent(): number {
  try {
    const saved = localStorage.getItem(LS_DETAIL_PCT);
    const parsed = saved ? Number(saved) : NaN;
    if (Number.isFinite(parsed)) return Math.max(DETAIL_PCT_MIN, Math.min(DETAIL_PCT_MAX, parsed));
  } catch { /* ignore */ }
  return DETAIL_PCT_DEFAULT;
}

function safeWrite(key: string, value: string): void {
  try { localStorage.setItem(key, value); } catch { /* best-effort */ }
}
