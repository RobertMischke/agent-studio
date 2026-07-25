import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  signal,
  viewChild,
} from '@angular/core';
import {
  cellOrder,
  createSmileyMask,
  EMPTY_STATE_CELL,
  EMPTY_STATE_COLS,
  EMPTY_STATE_CYCLE_MS,
  EMPTY_STATE_FRAME_MS,
  EMPTY_STATE_GAP,
  EMPTY_STATE_ROWS,
  EMPTY_STATE_STEP_MS,
  emptyStateFrame,
  type EmptyStatePhase,
} from './studio-empty-state.animation';

const MAX_AGE = 6;

@Component({
  selector: 'app-studio-empty-state',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './studio-empty-state.component.html',
  styleUrl: './studio-empty-state.component.scss',
})
export class StudioEmptyStateComponent implements AfterViewInit, OnDestroy {
  private readonly canvasRef = viewChild.required<ElementRef<HTMLCanvasElement>>('canvas');

  readonly phase = signal<EmptyStatePhase>('chaos');
  readonly phaseProgress = signal(0);
  readonly reducedMotion = signal(false);

  private grid = new Uint8Array(EMPTY_STATE_COLS * EMPTY_STATE_ROWS);
  private nextGrid = new Uint8Array(EMPTY_STATE_COLS * EMPTY_STATE_ROWS);
  private readonly formationGrid = new Uint8Array(EMPTY_STATE_COLS * EMPTY_STATE_ROWS);
  private readonly smileyMask = createSmileyMask();
  private readonly age = new Uint8Array(EMPTY_STATE_COLS * EMPTY_STATE_ROWS);
  private ctx: CanvasRenderingContext2D | null = null;

  private rafId = 0;
  private lastStep = 0;
  private lastRender = 0;
  private cycleStartedAt = 0;
  private running = false;
  private onScreen = true;
  private pageVisible = true;
  private stale = 0;
  private previousPhase: EmptyStatePhase = 'chaos';
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

    if (this.reducedMotion()) {
      this.phase.set('smiley');
      this.phaseProgress.set(1);
      this.render('smiley', 1);
      return;
    }

    document.addEventListener('visibilitychange', this.onVisibility);
    this.pageVisible = document.visibilityState === 'visible';

    if ('IntersectionObserver' in window) {
      this.io = new IntersectionObserver(
        entries => {
          this.onScreen = entries.some(entry => entry.isIntersecting);
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
    this.io?.disconnect();
    if (typeof document !== 'undefined') {
      document.removeEventListener('visibilitychange', this.onVisibility);
    }
  }

  private setupCanvas(canvas: HTMLCanvasElement): void {
    const dpr = Math.min(window.devicePixelRatio || 1, 2);
    const width = EMPTY_STATE_COLS * (EMPTY_STATE_CELL + EMPTY_STATE_GAP);
    const height = EMPTY_STATE_ROWS * (EMPTY_STATE_CELL + EMPTY_STATE_GAP);
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
    canvas.style.setProperty('--empty-canvas-width', `${width}px`);
    this.ctx?.scale(dpr, dpr);
  }

  private seed(): void {
    for (let index = 0; index < this.grid.length; index++) {
      const alive = Math.random() < 0.28 ? 1 : 0;
      this.grid[index] = alive;
      this.age[index] = alive ? 1 : 0;
    }
    this.spawnGlider(2, 2);
    this.spawnGlider(54, 18);
    this.stale = 0;
  }

  private spawnGlider(originX: number, originY: number): void {
    const cells = [[1, 0], [2, 1], [0, 2], [1, 2], [2, 2]];
    for (const [dx, dy] of cells) {
      const x = (originX + dx) % EMPTY_STATE_COLS;
      const y = (originY + dy) % EMPTY_STATE_ROWS;
      this.grid[y * EMPTY_STATE_COLS + x] = 1;
    }
  }

  private syncRunning(): void {
    const shouldRun = this.onScreen && this.pageVisible && !this.reducedMotion();
    if (shouldRun) this.start();
    else this.stop();
  }

  private start(): void {
    if (this.running) return;
    this.running = true;
    this.cycleStartedAt = performance.now();
    this.lastStep = this.cycleStartedAt;
    this.phaseProgress.set(0);
    this.render('chaos', 0);
    this.rafId = requestAnimationFrame(this.loop);
  }

  private stop(): void {
    this.running = false;
    if (this.rafId) cancelAnimationFrame(this.rafId);
    this.rafId = 0;
  }

  private readonly loop = (timestamp: number): void => {
    if (!this.running) return;
    this.rafId = requestAnimationFrame(this.loop);

    const elapsed = (timestamp - this.cycleStartedAt) % EMPTY_STATE_CYCLE_MS;
    const frame = emptyStateFrame(elapsed);
    this.handlePhaseChange(frame.phase);
    this.phaseProgress.set(Math.round(frame.progress * 10) / 10);

    if (frame.phase === 'chaos' && timestamp - this.lastStep >= EMPTY_STATE_STEP_MS) {
      this.lastStep = timestamp;
      this.stepLife();
    }
    if (timestamp - this.lastRender >= EMPTY_STATE_FRAME_MS) {
      this.lastRender = timestamp;
      this.render(frame.phase, frame.progress);
    }
  };

  private handlePhaseChange(nextPhase: EmptyStatePhase): void {
    if (nextPhase === this.previousPhase) return;
    if (nextPhase === 'forming') this.formationGrid.set(this.grid);
    if (nextPhase === 'chaos' && this.previousPhase === 'decay') this.seed();
    this.previousPhase = nextPhase;
    this.phase.set(nextPhase);
  }

  private stepLife(): void {
    let live = 0;
    let changed = 0;
    for (let y = 0; y < EMPTY_STATE_ROWS; y++) {
      for (let x = 0; x < EMPTY_STATE_COLS; x++) {
        const index = y * EMPTY_STATE_COLS + x;
        const neighbours = this.neighbours(x, y);
        const wasAlive = this.grid[index];
        const isAlive = wasAlive
          ? (neighbours === 2 || neighbours === 3 ? 1 : 0)
          : (neighbours === 3 ? 1 : 0);
        this.nextGrid[index] = isAlive;
        if (isAlive) {
          live++;
          this.age[index] = wasAlive ? Math.min(this.age[index] + 1, MAX_AGE) : 1;
        } else {
          this.age[index] = 0;
        }
        if (isAlive !== wasAlive) changed++;
      }
    }
    [this.grid, this.nextGrid] = [this.nextGrid, this.grid];
    if (live < 8 || changed === 0) {
      if (++this.stale > 3) this.seed();
    } else {
      this.stale = 0;
    }
  }

  private neighbours(x: number, y: number): number {
    let count = 0;
    for (let dy = -1; dy <= 1; dy++) {
      for (let dx = -1; dx <= 1; dx++) {
        if (dx === 0 && dy === 0) continue;
        const nx = (x + dx + EMPTY_STATE_COLS) % EMPTY_STATE_COLS;
        const ny = (y + dy + EMPTY_STATE_ROWS) % EMPTY_STATE_ROWS;
        count += this.grid[ny * EMPTY_STATE_COLS + nx];
      }
    }
    return count;
  }

  private render(phase: EmptyStatePhase, progress: number): void {
    const ctx = this.ctx;
    if (!ctx) return;
    const canvas = this.canvasRef().nativeElement;
    const width = EMPTY_STATE_COLS * (EMPTY_STATE_CELL + EMPTY_STATE_GAP);
    const height = EMPTY_STATE_ROWS * (EMPTY_STATE_CELL + EMPTY_STATE_GAP);
    ctx.clearRect(0, 0, width, height);
    ctx.fillStyle = getComputedStyle(canvas).color;

    for (let index = 0; index < this.grid.length; index++) {
      const alpha = this.cellAlpha(index, phase, progress);
      if (alpha <= 0) continue;
      const x = index % EMPTY_STATE_COLS;
      const y = Math.floor(index / EMPTY_STATE_COLS);
      ctx.globalAlpha = alpha;
      ctx.beginPath();
      ctx.arc(
        x * (EMPTY_STATE_CELL + EMPTY_STATE_GAP) + EMPTY_STATE_CELL / 2,
        y * (EMPTY_STATE_CELL + EMPTY_STATE_GAP) + EMPTY_STATE_CELL / 2,
        EMPTY_STATE_CELL / 2,
        0,
        Math.PI * 2,
      );
      ctx.fill();
    }
    ctx.globalAlpha = 1;
  }

  private cellAlpha(index: number, phase: EmptyStatePhase, progress: number): number {
    const order = cellOrder(index);
    if (phase === 'smiley') return this.smileyMask[index] ? 0.94 : 0;
    if (phase === 'forming') {
      const reveal = this.smileyMask[index] && progress > order * 0.82 ? Math.min(1, progress * 1.5) : 0;
      const dissolve = this.formationGrid[index] ? Math.max(0, 1 - progress * (0.65 + order * 0.7)) : 0;
      return Math.max(reveal, dissolve);
    }
    if (phase === 'decay') {
      if (!this.smileyMask[index] || progress > order * 0.82 + 0.12) return 0;
      return 0.94 * Math.max(0, 1 - progress);
    }
    if (!this.grid[index]) return 0;
    return 0.35 + 0.65 * (1 - (this.age[index] - 1) / MAX_AGE);
  }
}
