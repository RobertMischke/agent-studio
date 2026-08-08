import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { TaskExecutionLocation } from '../../models/task.model';

@Component({
  selector: 'app-remote-dispatch-rejection',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './remote-dispatch-rejection.component.html',
  styleUrl: './remote-dispatch-rejection.component.scss',
})
export class RemoteDispatchRejectionComponent {
  readonly execution = input<TaskExecutionLocation | null | undefined>(null);
  readonly compact = input(false);
  readonly rejection = computed(() => this.execution()?.lastRejection ?? null);
  readonly runnerLabel = computed(() => {
    const rejection = this.rejection();
    return rejection?.runnerName || rejection?.runnerId || 'Remote Runner';
  });
}
