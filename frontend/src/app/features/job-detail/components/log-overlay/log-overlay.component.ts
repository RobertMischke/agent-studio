import {
  AfterViewInit,
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  OnDestroy,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import { CliOutputLine, JobLogEntry } from '../../../../models/job.model';
import { ActivityLogViewComponent } from '../activity-log-view/activity-log-view';
import { formatTime as fmtTime } from '../../../../services/format.util';
import { copyTextToClipboard } from '../../../../services/clipboard.util';

import { TooltipDirective } from '../../../../components/tooltip';
/**
 * Modal overlay that shows the live CLI output and the parsed protocol
 * log side-by-side. Triggered from the protocol pane's "Maximize log"
 * button. Uses a native <dialog> via showModal() so the browser owns
 * focus trapping, top-layer rendering, and ESC handling. Backdrop click
 * also closes.
 */
@Component({
  selector: 'app-log-overlay',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ActivityLogViewComponent, TooltipDirective],
  templateUrl: './log-overlay.component.html'
})
export class LogOverlayComponent implements AfterViewInit, OnDestroy {
  readonly cliOutput = input<CliOutputLine[]>([]);
  readonly log = input<JobLogEntry[]>([]);
  readonly isRunning = input(false);

  readonly close = output<void>();

  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');
  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;

  private readonly dlg = viewChild<ElementRef<HTMLDialogElement>>('dlg');

  ngAfterViewInit(): void {
    const el = this.dlg()?.nativeElement;
    if (el && !el.open && typeof el.showModal === 'function') {
      el.showModal();
    }
  }

  ngOnDestroy(): void {
    if (this.copyResetTimer !== null) {
      clearTimeout(this.copyResetTimer);
      this.copyResetTimer = null;
    }
    const el = this.dlg()?.nativeElement;
    if (el?.open) el.close();
  }

  dismiss(): void {
    const el = this.dlg()?.nativeElement;
    if (el?.open) {
      el.close(); // fires `close` event -> emits this.close
    } else {
      this.close.emit();
    }
  }

  /**
   * <dialog> click events fire on the dialog element itself when the user
   * clicks the backdrop area outside the panel. We stop propagation on the
   * panel, so any click reaching the dialog is a backdrop click.
   */
  onBackdropClick(event: MouseEvent): void {
    if (event.target === this.dlg()?.nativeElement) {
      this.dismiss();
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
