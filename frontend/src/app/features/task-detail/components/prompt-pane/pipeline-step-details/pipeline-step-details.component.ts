import { ChangeDetectionStrategy, Component, input, signal } from '@angular/core';
import { DialogComponent } from '../../../../../components/dialog/dialog.component';
import { StudioIconComponent } from '../../../../../components/studio-icon/studio-icon.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { PipelineStepResultComponent, type PipelineStepResultHeader } from '../pipeline-step-result/pipeline-step-result.component';
import { TaskPromptPopoverComponent } from '../task-prompt-popover/task-prompt-popover.component';

@Component({
  selector: 'app-pipeline-step-details',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DialogComponent, PipelineStepResultComponent, StudioIconComponent, TaskPromptPopoverComponent, TooltipDirective],
  templateUrl: './pipeline-step-details.component.html',
  styleUrl: './pipeline-step-details.component.scss',
})
export class PipelineStepDetailsComponent {
  readonly stepId = input.required<string>();
  readonly label = input.required<string>();
  readonly docs = input.required<string>();
  readonly promptMarkdown = input('');
  readonly jobId = input.required<string>();
  readonly watchPath = input<string | null>(null);
  readonly resultFile = input<string | null>(null);
  readonly resultHeader = input<PipelineStepResultHeader | null>(null);
  readonly concernTitle = input<string | null>(null);
  readonly concernBody = input<string | null>(null);

  readonly open = signal(false);

  show(): void {
    this.open.set(true);
  }

  close(): void {
    this.open.set(false);
  }
}
