import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { TooltipDirective } from 'coding-agent-chat/shared';

@Component({
  selector: 'app-pipeline-step-toggle',
  standalone: true,
  imports: [FormsModule, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-step-toggle.component.html',
  styleUrl: './pipeline-step-toggle.component.scss',
})
export class PipelineStepToggleComponent {
  readonly stepId = input.required<string>();
  readonly stepName = input.required<string>();
  readonly enabled = input.required<boolean>();
  readonly canDisable = input.required<boolean>();
  readonly busy = input(false);
  readonly enabledChange = output<boolean>();

  tooltip(): string {
    if (!this.canDisable()) return 'Fixed catalogue step; always on.';
    return this.enabled()
      ? 'Step runs for this project. Click to skip it.'
      : 'Step is skipped. Click to enable.';
  }
}
