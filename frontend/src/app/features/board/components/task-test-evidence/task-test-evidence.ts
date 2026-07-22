import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskTestRunEvidence } from '../../../../models/task.model';

@Component({
  selector: 'app-task-test-evidence',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './task-test-evidence.html',
  styleUrl: './task-test-evidence.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskTestEvidenceComponent {
  readonly evidence = input.required<TaskTestRunEvidence>();
}
