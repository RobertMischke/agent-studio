import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { REJECTION_CODE_BUILD_PROFILE_GATE, type TaskExecutionLocation } from '../../models/task.model';

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

  /**
   * A closed build-profile gate is a project setting, not a runner verdict
   * (AGT-2677). Attributing it to the runner sends the operator to restart hosts,
   * which is the detour the Quality Studio outage actually took.
   */
  readonly lead = computed(() =>
    this.rejection()?.code === REJECTION_CODE_BUILD_PROFILE_GATE
      ? 'Project build profile not validated:'
      : `Runner ${this.runnerLabel()} rejected:`);
}
