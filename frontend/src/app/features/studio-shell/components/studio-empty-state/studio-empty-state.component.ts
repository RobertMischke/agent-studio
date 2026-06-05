import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  signal,
  viewChild,
} from '@angular/core';

/**
 * Idle empty-state for the studio editor surface, shown when every tab is
 * closed. A tiny Conway's Game of Life runs on a `<canvas>` ("code +
 * animation" in its purest form) with a rotating funny subtitle.
 *
 * Constraints (task ASS / empty-state):
 * - Pure canvas, no libraries.
 * - Pauses when off-screen (IntersectionObserver) or when the tab is in the
 *   background (`visibilitychange`); cheap when idle.
 * - Respects `prefers-reduced-motion`: renders a single static frame and
 *   never starts the animation loop or the subtitle rotation.
 * - Colours come from the central design tokens (ASS-737): the canvas
 *   inherits `--studio-accent` via CSS `color`, read back as `currentColor`,
 *   so light + dark themes are handled without per-theme JS.
 */
const COLS = 40;
const ROWS = 24;
const CELL = 7; // logical px per cell
const GAP = 1;
const STEP_MS = 110; // ~9 fps
const MAX_AGE = 6;

const SUBTITLES: readonly string[] = [
  'No tabs open - the cells keep themselves busy.',
  'Idle. Even the agents are taking a break.',
  '404 tabs found. Have some cellular automata instead.',
  'Nothing running. The board is one click away.',
  '// TODO: open a tab',
  'git commit -m "nothing to do"',
];

@Component({
  selector: 'app-studio-empty-state',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './studio-empty-state.component.html',
  styleUrl: './studio-empty-state.component.scss',
})
export class StudioEmptyStateComponent implements AfterViewInit, OnDestroy {
  private readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');

  readonly subtitle = signal(SUBTITLES[0]);
  readonly reducedMotion = signal(false);

  private grid = new Uint8Array(COLS * ROWS);
  private nextGrid = new Uint8Array(COLS * ROWS);
  private age = new Uint8Array(COLS * ROWS);
  private ctx: CanvasRenderingContext2D | null = null;

  private rafId = 0;
  private lastStep = 0;
  private running = false;
  private onScreen = true;
  private pageVisible = true;
  private stale = 0;
  private subtitleTimer = 0;
  private io?: IntersectionObserver;

  private readonly onVisibility = (): void => {
    this.pageVisible = typeof document === 'undefined' || document.visibilityState === 'visible';
    this.syncRunning();
  };

  ngAfterViewInit(): void {
    const canvas = this.canvasRef().nativeElement;
    this.ctx = canvas.getContext('2d');
    this.reducedMotion.set(!!window.matchMedia?.('(prefers-reduced-motion: reduce)')?.matches);

    this.setupCanvas(canvas);
    this.seed();
    this.render();

    // Reduced motion: a single static frame, no loop, no rotation.
    if (this.reducedMotion()) return;

    this.subtitle.set(SUBTITLES[Math.floor(Math.random() * SUBTITLES.length)]);
    this.subtitleTimer = window.setInterval(() => this.rotateSubtitle(), 6000);

    document.addEventListener('visibilitychange', this.onVisibility);
    this.pageVisible = document.visibilityState === 'visible';

    if ('IntersectionObserver' in window) {
      this.io = new IntersectionObserver(
        entries => {
          this.onScreen = entries.some(e => e.isIntersecting);
          this.syncRunning();
        },
        { threshold: 0.01 },
      );
      this.io.observe(canvas);
    }
    this.syncRunning();
  }

  ngOnDestroy(): void {
    this.stop();
    window.clearInterval(this.subtitleTimer);
    this.io?.disconnect();
    if (typeof document !== 'undefined') {
      document.removeEventListener('visibilitychange', this.onVisibility);
    }
  }

  private setupCanvas(canvas: HTMLCanvasElement): void {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const w = COLS * (CELL + GAP);
    const h = ROWS * (CELL + GAP);
    canvas.width = Math.round(w * dpr);
    canvas.height = Math.round(h * dpr);
    canvas.style.width = `${w}px`;
    canvas.style.height = `${h}px`;
    this.ctx?.scale(dpr, dpr);
  }

  private seed(): void {
    for (let i = 0; i < this.grid.length; i++) {
      const alive = Math.random() < 0.28 ? 1 : 0;
      this.grid[i] = alive;
      this.age[i] = alive ? 1 : 0;
    }
    // Drop a glider in the corner so there is always some motion to watch.
    this.spawnGlider(2, 2);
    this.stale = 0;
  }

  private spawnGlider(cx: number, cy: number): void {
    const cells = [[1, 0], [2, 1], [0, 2], [1, 2], [2, 2]];
    for (const [dx, dy] of cells) {
      const x = (cx + dx) % COLS;
      const y = (cy + dy) % ROWS;
      this.grid[y * COLS + x] = 1;
    }
  }

  private rotateSubtitle(): void {
    const cur = this.subtitle();
    let next = cur;
    while (next === cur) next = SUBTITLES[Math.floor(Math.random() * SUBTITLES.length)];
    this.subtitle.set(next);
  }

  private syncRunning(): void {
    const should = this.onScreen && this.pageVisible && !this.reducedMotion();
    if (should) this.start();
    else this.stop();
  }

  private start(): void {
    if (this.running) return;
    this.running = true;
    this.rafId = requestAnimationFrame(this.loop);
  }

  private stop(): void {
    this.running = false;
    if (this.rafId) cancelAnimationFrame(this.rafId);
    this.rafId = 0;
  }

  private readonly loop = (ts: number): void => {
    if (!this.running) return;
    this.rafId = requestAnimationFrame(this.loop);
    if (ts - this.lastStep < STEP_MS) return;
    this.lastStep = ts;
    this.step();
    this.render();
  };

  private step(): void {
    let live = 0;
    let changed = 0;
    for (let y = 0; y < ROWS; y++) {
      for (let x = 0; x < COLS; x++) {
        const idx = y * COLS + x;
        const n = this.neighbours(x, y);
        const was = this.grid[idx];
        const now = was ? (n === 2 || n === 3 ? 1 : 0) : (n === 3 ? 1 : 0);
        this.nextGrid[idx] = now;
        if (now) {
          live++;
          this.age[idx] = was ? Math.min(this.age[idx] + 1, MAX_AGE) : 1;
        } else {
          this.age[idx] = 0;
        }
        if (now !== was) changed++;
      }
    }
    const tmp = this.grid;
    this.grid = this.nextGrid;
    this.nextGrid = tmp;

    // Reseed when the colony dies out or freezes into a still life so the
    // animation never settles into a boring static frame.
    if (live < 6 || changed === 0) {
      if (++this.stale > 3) this.seed();
    } else {
      this.stale = 0;
    }
  }

  private neighbours(x: number, y: number): number {
    let n = 0;
    for (let dy = -1; dy <= 1; dy++) {
      for (let dx = -1; dx <= 1; dx++) {
        if (dx === 0 && dy === 0) continue;
        const nx = (x + dx + COLS) % COLS;
        const ny = (y + dy + ROWS) % ROWS;
        n += this.grid[ny * COLS + nx];
      }
    }
    return n;
  }

  private render(): void {
    const ctx = this.ctx;
    if (!ctx) return;
    const canvas = this.canvasRef().nativeElement;
    const color = getComputedStyle(canvas).color || '#e08a3c';
    const w = COLS * (CELL + GAP);
    const h = ROWS * (CELL + GAP);
    ctx.clearRect(0, 0, w, h);
    ctx.fillStyle = color;
    for (let y = 0; y < ROWS; y++) {
      for (let x = 0; x < COLS; x++) {
        const idx = y * COLS + x;
        if (!this.grid[idx]) continue;
        // Newly born cells are brightest; survivors fade toward a dim glow,
        // giving the colony a hypnotic "comet trail" texture.
        ctx.globalAlpha = 0.35 + 0.65 * (1 - (this.age[idx] - 1) / MAX_AGE);
        ctx.fillRect(x * (CELL + GAP), y * (CELL + GAP), CELL, CELL);
      }
    }
    ctx.globalAlpha = 1;
  }
}
