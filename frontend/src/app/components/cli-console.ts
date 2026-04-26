import { Component, ElementRef, ViewChild, input, signal, computed, effect, OnDestroy } from '@angular/core';
import { CliOutputLine } from '../models/job.model';

@Component({
  selector: 'app-cli-console',
  standalone: true,
  template: `
    <div class="console">
      <div class="console__header">
        <span class="console__title">{{ title() }}</span>
        <div class="console__controls">
          <button class="console__btn" [class.console__btn--active]="filterStream() === 'all'" (click)="filterStream.set('all')">All</button>
          <button class="console__btn" [class.console__btn--active]="filterStream() === 'stdout'" (click)="filterStream.set('stdout')">stdout</button>
          <button class="console__btn" [class.console__btn--active]="filterStream() === 'stderr'" (click)="filterStream.set('stderr')">stderr</button>
          <span class="console__separator">|</span>
          <button class="console__btn" [class.console__btn--active]="autoScroll()" (click)="autoScroll.set(!autoScroll())">
            {{ autoScroll() ? '📌' : '📋' }} Auto-scroll
          </button>
          <button class="console__btn" (click)="copyOutput()">{{ copied() ? '✅ Copied' : '📋 Copy' }}</button>
          <button class="console__btn" (click)="clear()">🗑 Clear</button>
        </div>
      </div>
      <div class="console__body" #scrollContainer [style.max-height]="bodyMaxHeight()">
        @for (line of filteredLines(); track $index) {
          <div class="console__line" [class]="'console__line--' + line.stream">
            <span class="console__time">{{ formatTime(line.timestamp) }}</span>
            <span class="console__stream">{{ line.stream === 'stderr' ? 'ERR' : 'OUT' }}</span>
            <span class="console__text">{{ line.text }}</span>
          </div>
        }
        @if (filteredLines().length === 0) {
          <div class="console__empty">
            {{ lines().length === 0 ? 'No output yet. Start the job to see CLI output here.' : 'No lines match the current filter.' }}
          </div>
        }
      </div>
      <div class="console__footer">
        <span class="console__count">{{ filteredLines().length }} / {{ lines().length }} lines</span>
      </div>
    </div>
  `,
  styles: [`
    .console {
      background: #0d0d1a;
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 8px;
      overflow: hidden;
      display: flex;
      flex-direction: column;
    }
    .console__header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      padding: 8px 12px;
      background: rgba(255,255,255,0.03);
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .console__title {
      font-size: 11px;
      text-transform: uppercase;
      letter-spacing: 0.5px;
      color: #64748b;
      font-weight: 600;
    }
    .console__controls { display: flex; gap: 4px; align-items: center; }
    .console__separator { color: #333; margin: 0 4px; }
    .console__btn {
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.06);
      color: #64748b;
      padding: 2px 8px;
      border-radius: 4px;
      cursor: pointer;
      font-size: 11px;
    }
    .console__btn:hover { background: rgba(255,255,255,0.08); color: #94a3b8; }
    .console__btn--active { background: rgba(99,102,241,0.15); border-color: rgba(99,102,241,0.3); color: #a5b4fc; }
    .console__body {
      overflow-y: auto;
      padding: 8px 0;
      font-family: 'Consolas', 'Monaco', monospace;
      font-size: 12px;
      line-height: 1.5;
    }
    .console__line {
      display: flex;
      gap: 8px;
      padding: 1px 12px;
      align-items: baseline;
    }
    .console__line:hover { background: rgba(255,255,255,0.03); }
    .console__line--stdout .console__text { color: #e2e8f0; }
    .console__line--stderr .console__text { color: #f87171; }
    .console__time {
      color: #475569;
      font-size: 10px;
      min-width: 65px;
      font-variant-numeric: tabular-nums;
    }
    .console__stream {
      font-size: 9px;
      font-weight: 700;
      min-width: 28px;
      text-align: center;
      padding: 1px 4px;
      border-radius: 3px;
    }
    .console__line--stdout .console__stream { background: rgba(34,197,94,0.1); color: #4ade80; }
    .console__line--stderr .console__stream { background: rgba(248,113,113,0.1); color: #f87171; }
    .console__text { white-space: pre-wrap; word-break: break-all; }
    .console__empty {
      padding: 24px 12px;
      text-align: center;
      color: #475569;
      font-size: 12px;
    }
    .console__footer {
      padding: 4px 12px;
      border-top: 1px solid rgba(255,255,255,0.04);
      font-size: 10px;
      color: #475569;
    }
    .console__count { font-variant-numeric: tabular-nums; }
  `]
})
export class CliConsoleComponent implements OnDestroy {
  readonly lines = input<CliOutputLine[]>([]);
  readonly title = input('Console Output');
  readonly bodyMaxHeight = input('400px');
  readonly filterStream = signal<'all' | 'stdout' | 'stderr'>('all');
  readonly autoScroll = signal(true);
  readonly filteredLines = computed(() => {
    const all = this.lines();
    const filter = this.filterStream();
    return filter === 'all' ? all : all.filter((line) => line.stream === filter);
  });

  @ViewChild('scrollContainer') scrollContainer!: ElementRef<HTMLDivElement>;
  private scrollTimer: ReturnType<typeof setTimeout> | null = null;

  private scrollEffect = effect(() => {
    if (this.scrollTimer) {
      clearTimeout(this.scrollTimer);
      this.scrollTimer = null;
    }

    this.filteredLines();
    if (this.autoScroll()) {
      this.scrollTimer = setTimeout(() => {
        const el = this.scrollContainer?.nativeElement;
        if (el) el.scrollTop = el.scrollHeight;
        this.scrollTimer = null;
      }, 0);
    }
  });

  ngOnDestroy() {
    this.scrollEffect.destroy();
    if (this.scrollTimer) clearTimeout(this.scrollTimer);
  }

  formatTime(dateStr: string): string {
    const d = new Date(dateStr);
    return d.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit', second: '2-digit' });
  }

  readonly copied = signal(false);
  private copiedTimer: ReturnType<typeof setTimeout> | null = null;

  copyOutput() {
    const text = this.filteredLines().map(l => l.text).join('\n');
    navigator.clipboard.writeText(text).then(() => {
      this.copied.set(true);
      if (this.copiedTimer) clearTimeout(this.copiedTimer);
      this.copiedTimer = setTimeout(() => this.copied.set(false), 2000);
    });
  }

  clear() {
    // Can't clear input, but we can filter to show nothing useful
    this.filterStream.set('all');
  }
}
