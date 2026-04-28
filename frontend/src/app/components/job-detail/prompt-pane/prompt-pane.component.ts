import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { MarkdownRichEditorComponent } from '../../markdown-rich-editor';

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
  readonly maximized = input(false);
  readonly weight = input<number>(1);
  readonly isRunning = input(false);

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();
  readonly save = output<string>();
}
