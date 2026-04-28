import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { CliOutputLine, JobLogEntry } from '../../../models/job.model';
import { ActivityLogViewComponent } from '../../activity-log-view';
import { formatTime as fmtTime } from '../../../services/format.util';

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
export class LogOverlayComponent {
  readonly cliOutput = input<CliOutputLine[]>([]);
  readonly log = input<JobLogEntry[]>([]);

  readonly close = output<void>();

  formatTime(dateStr: string): string { return fmtTime(dateStr); }
}
