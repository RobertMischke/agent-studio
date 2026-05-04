import { ChangeDetectionStrategy, Component, OnDestroy, input, output, signal } from '@angular/core';
import { CliOutputLine, JobLogEntry } from '../../../models/job.model';
import { ActivityLogViewComponent } from '../../activity-log-view';
import { formatTime as fmtTime } from '../../../services/format.util';
import { copyTextToClipboard } from '../../../services/clipboard.util';

/**
 * Modal overlay that shows the live CLI output and the parsed protocol
 * log side-by-side. Triggered from the protocol pane's "Maximize log"
 * button. Backdrop click closes.
 */
@Component({
  selector: 'app-log-overlay',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ActivityLogViewComponent],
  templateUrl: './log-overlay.component.html'
})
export class LogOverlayComponent implements OnDestroy {
  readonly cliOutput = input<CliOutputLine[]>([]);
  readonly log = input<JobLogEntry[]>([]);
  readonly isRunning = input(false);

  readonly close = output<void>();

  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;

  ngOnDestroy(): void {
    if (this.copyResetTimer !== null) {
      clearTimeout(this.copyResetTimer);
      this.copyResetTimer = null;
    }
  }

  formatTime(dateStr: string): string { return fmtTime(dateStr); }

  copyLabel(): string {
    const s = this.copyState();
    if (s === 'copied') return '✓ Copied';
    if (s === 'failed') return '⚠ Copy failed';
    return '📋 Copy';
  }

  async copyProtocol(): Promise<void> {
    const text = this.log()
      .map((e) => {
        const head = `[${this.formatTime(e.timestamp)}] ${e.event}`;
        return e.detail ? `${head} — ${e.detail}` : head;
      })
      .join('\n');
    if (!text) return;
    const ok = await copyTextToClipboard(text);
    this.copyState.set(ok ? 'copied' : 'failed');
    if (this.copyResetTimer !== null) clearTimeout(this.copyResetTimer);
    this.copyResetTimer = setTimeout(() => {
      this.copyState.set('idle');
      this.copyResetTimer = null;
    }, 2000);
  }
}
