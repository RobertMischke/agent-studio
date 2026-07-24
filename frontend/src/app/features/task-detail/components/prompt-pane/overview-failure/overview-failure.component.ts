import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { TaskOutcomeIssue } from '../../../../../models/task.model';
import { presentOutcomeFailure } from './outcome-failure-presentation';

@Component({
  selector: 'app-overview-failure',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './overview-failure.component.html',
  styleUrl: './overview-failure.component.scss',
})
export class OverviewFailureComponent {
  readonly issue = input.required<TaskOutcomeIssue>();
  readonly presentation = computed(() => presentOutcomeFailure(this.issue()));
}
