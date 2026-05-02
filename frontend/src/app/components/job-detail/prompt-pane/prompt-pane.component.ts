import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MarkdownRichEditorComponent } from '../../markdown-rich-editor';
import { JobPromptHistoryEntry } from '../../../models/job.model';
import { markdownToHtml } from '../../markdown-utils';

/**
 * Prompt pane of the job-detail view: renders prompt.md inside the
 * shared markdown-rich-editor. Edit lock is driven by the parent's
 * "isRunning" flag.
 */
@Component({
  selector: 'app-prompt-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownRichEditorComponent],
  templateUrl: './prompt-pane.component.html',
  styleUrls: ['./prompt-pane.component.scss']
})
export class PromptPaneComponent {
  readonly markdown = input<string>('');
  readonly history = input<JobPromptHistoryEntry[]>([]);
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();
  readonly save = output<string>();

  renderMarkdown(md: string): string {
    return markdownToHtml(md ?? '');
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString();
  }
}
