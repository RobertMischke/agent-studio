import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MarkdownRichEditorComponent } from '../../../../components/markdown-rich-editor';
import { JobPromptHistoryEntry, JobTitleHistoryEntry } from '../../../../models/job.model';
import { markdownToHtml } from '../../../../components/markdown-utils';
import { MarkdownImageLightboxDirective } from '../../../../directives/markdown-image-lightbox.directive';
import { resolveProtocolImageSrc } from '../protocol-pane/protocol-image-resolver';

import { TooltipDirective } from '../../../../components/tooltip';
/**
 * Prompt pane of the job-detail view: renders prompt.md inside the
 * shared markdown-rich-editor. Edit lock is driven by the parent's
 * "isRunning" flag. Per the user's correction, Evidence content
 * stays in the Protocol pane (not duplicated here).
 */
@Component({
  selector: 'app-prompt-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [MarkdownRichEditorComponent, MarkdownImageLightboxDirective, TooltipDirective],
  templateUrl: './prompt-pane.component.html',
  styleUrls: ['./prompt-pane.component.scss']
})
export class PromptPaneComponent {
  readonly markdown = input<string>('');
  readonly history = input<JobPromptHistoryEntry[]>([]);
  readonly titleHistory = input<JobTitleHistoryEntry[]>([]);
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();
  readonly save = output<string>();

  renderMarkdown(md: string): string {
    const jobId = this.jobId();
    const watchPath = this.watchPath();
    return markdownToHtml(md ?? '', {
      resolveImageSrc: (src) => resolveProtocolImageSrc(src, jobId, watchPath),
    });
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return iso;
    return d.toLocaleString();
  }
}
