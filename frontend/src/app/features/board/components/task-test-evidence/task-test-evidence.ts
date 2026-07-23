import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { TaskTestRunEvidence } from '../../../../models/task.model';

@Component({
  selector: 'app-task-test-evidence',
  standalone: true,
  imports: [AppTooltipDirective],
  templateUrl: './task-test-evidence.html',
  styleUrl: './task-test-evidence.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskTestEvidenceComponent {
  readonly evidence = input.required<TaskTestRunEvidence>();
}
