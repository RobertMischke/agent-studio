import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';

@Component({
  selector: 'app-pipeline-step-row-state',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './pipeline-step-row-state.component.html',
  styleUrl: './pipeline-step-row-state.component.scss',
  host: {
    '(click)': '$event.stopPropagation()',
    '(keydown)': '$event.stopPropagation()',
  },
})
export class PipelineStepRowStateComponent {
  readonly stepId = input.required<string>();
  readonly framework = input('');
  readonly canDisable = input.required<boolean>();
  readonly enabled = input.required<boolean>();
  readonly disabled = input(false);
  readonly enabledChange = output<boolean>();

  onChange(event: Event): void {
    this.enabledChange.emit((event.target as HTMLInputElement).checked);
  }
}
