import { AfterViewChecked, Component, ElementRef, EventEmitter, OnDestroy, Output, signal, ViewChild } from '@angular/core';

interface ConsoleLine {
  kind: 'log' | 'stdout' | 'stderr' | 'error' | 'done';
  text: string;
}

/**
 * Modal console that streams update-stable.sh via SSE
 * (GET /api/devtools/update-stable/stream). One server event = one line;
 * we push lines into a signal and pin the scroll to the bottom.
 *
 * Closing the dialog mid-run aborts the EventSource which closes the HTTP
 * stream; the backend kills the child process when the request is cancelled.
 */
@Component({
  selector: 'app-update-stable-console',
  standalone: true,
  template: `
    <div class="overlay" (click)="onBackdropClick()">
      <div class="panel" (click)="$event.stopPropagation()">
        <header class="panel__head">
          <div>
            <div class="panel__eyebrow">Dev tool</div>
            <h2 class="panel__title">Update Stable</h2>
            <p class="panel__sub">Runs update-stable.sh and streams output live.</p>
          </div>
          <button class="panel__close" (click)="closed.emit()" title="Close">×</button>
        </header>
        <div class="panel__body">
          <div class="actions">
            <button class="btn btn--primary"
                    data-testid="update-stable-run"
                    [disabled]="running()"
                    (click)="start()">
              {{ running() ? 'Running…' : 'Run update-stable.sh' }}
            </button>
            <button class="btn btn--ghost"
                    [disabled]="running() || lines().length === 0"
                    (click)="clear()">Clear</button>
            @if (running()) {
              <button class="btn btn--danger" (click)="abort()">Abort</button>
            }
            <span class="status"
                  [class.status--ok]="exitCode() === 0"
                  [class.status--err]="exitCode() !== null && exitCode() !== 0">
              @if (exitCode() === null && running()) {
                Streaming…
              } @else if (exitCode() === 0) {
                Done (exit 0)
              } @else if (exitCode() !== null) {
                Failed (exit {{ exitCode() }})
              } @else {
                Idle
              }
            </span>
          </div>
          <pre #log class="console" data-testid="update-stable-log">@for (line of lines(); track $index) {<span class="line line--{{ line.kind }}">{{ line.text }}</span>
}</pre>
        </div>
      </div>
    </div>
  `,
  styles: [`
    .overlay { position: fixed; inset: 0; background: rgba(0,0,0,0.7); display: grid; place-items: center; z-index: 200; }
    .panel { background: #11111b; border: 1px solid rgba(245,158,11,0.45); border-radius: 14px; width: min(900px, 94vw); max-height: 90vh; display: flex; flex-direction: column; box-shadow: 0 30px 100px rgba(0,0,0,0.5); }
    .panel__head { display: flex; justify-content: space-between; align-items: flex-start; padding: 18px 22px; border-bottom: 1px solid rgba(255,255,255,0.06); gap: 16px; }
    .panel__eyebrow { font-size: 11px; letter-spacing: 0.1em; text-transform: uppercase; color: #f59e0b; margin-bottom: 4px; }
    .panel__title { margin: 0; font-size: 20px; color: #f8fafc; }
    .panel__sub { margin: 4px 0 0; font-size: 12px; color: #94a3b8; }
    .panel__close { background: rgba(255,255,255,0.06); border: 1px solid rgba(255,255,255,0.1); color: #f8fafc; width: 32px; height: 32px; border-radius: 999px; cursor: pointer; font-size: 18px; }
    .panel__close:hover { background: rgba(255,255,255,0.12); }
    .panel__body { display: flex; flex-direction: column; gap: 12px; padding: 16px 22px 22px; min-height: 0; flex: 1; }
    .actions { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .btn { background: rgba(255,255,255,0.10); border: 1px solid rgba(255,255,255,0.18); color: #f8fafc; padding: 6px 14px; border-radius: 6px; cursor: pointer; font-size: 12px; font-weight: 600; }
    .btn:hover:not(:disabled) { background: rgba(255,255,255,0.18); }
    .btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .btn--primary { background: rgba(245,158,11,0.85); border-color: rgba(252,211,77,0.9); color: #1a1208; }
    .btn--primary:hover:not(:disabled) { background: rgba(252,211,77,0.95); }
    .btn--ghost { background: transparent; }
    .btn--danger { background: rgba(239,68,68,0.7); border-color: rgba(248,113,113,0.85); }
    .status { font-size: 12px; color: #94a3b8; margin-left: auto; }
    .status--ok { color: #4ade80; }
    .status--err { color: #f87171; }
    .console { flex: 1; min-height: 320px; max-height: 60vh; overflow: auto; margin: 0; padding: 12px 14px; background: #05050a; border: 1px solid rgba(255,255,255,0.08); border-radius: 8px; font-family: 'Consolas', 'SFMono-Regular', monospace; font-size: 12px; line-height: 1.5; color: #cbd5e1; white-space: pre-wrap; word-break: break-word; }
    .line { display: block; }
    .line--log { color: #93c5fd; }
    .line--stderr { color: #fca5a5; }
    .line--error { color: #fca5a5; font-weight: 700; }
    .line--done { color: #4ade80; font-weight: 700; }
  `]
})
export class UpdateStableConsoleComponent implements OnDestroy, AfterViewChecked {
  @Output() closed = new EventEmitter<void>();

  readonly lines = signal<ConsoleLine[]>([]);
  readonly running = signal(false);
  readonly exitCode = signal<number | null>(null);

  @ViewChild('log') logEl?: ElementRef<HTMLPreElement>;

  private source: EventSource | null = null;
  private prevLineCount = 0;

  ngOnDestroy(): void {
    this.abort();
  }

  ngAfterViewChecked(): void {
    if (!this.logEl) return;
    const count = this.lines().length;
    if (count === this.prevLineCount) return;
    this.prevLineCount = count;
    const el = this.logEl.nativeElement;
    el.scrollTop = el.scrollHeight;
  }

  onBackdropClick(): void {
    if (this.running()) return;
    this.closed.emit();
  }

  start(): void {
    if (this.running()) return;
    this.lines.set([]);
    this.exitCode.set(null);
    this.running.set(true);

    const src = new EventSource('/api/devtools/update-stable/stream');
    this.source = src;

    const push = (kind: ConsoleLine['kind']) => (e: MessageEvent) => {
      this.lines.update((arr) => [...arr, { kind, text: e.data }]);
    };
    src.addEventListener('log', push('log'));
    src.addEventListener('stdout', push('stdout'));
    src.addEventListener('stderr', push('stderr'));
    src.addEventListener('error', (e: MessageEvent) => {
      // Both protocol-level errors (no event payload) and our `event: error`
      // frames land here; `e.data` is only set for the latter.
      if (e.data) this.lines.update((arr) => [...arr, { kind: 'error', text: e.data }]);
      this.finish(null);
    });
    src.addEventListener('done', (e: MessageEvent) => {
      this.lines.update((arr) => [...arr, { kind: 'done', text: e.data }]);
      const m = /exit code (-?\d+)/.exec(e.data);
      this.finish(m ? Number(m[1]) : null);
    });
  }

  abort(): void {
    if (this.source) {
      this.source.close();
      this.source = null;
    }
    this.running.set(false);
  }

  clear(): void {
    this.lines.set([]);
    this.exitCode.set(null);
  }

  private finish(exit: number | null): void {
    if (this.source) { this.source.close(); this.source = null; }
    this.running.set(false);
    this.exitCode.set(exit);
  }
}
