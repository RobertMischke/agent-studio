import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { PendingButtonDirective } from '../../../../components/async-feedback';
import type { ProjectUrlProcessSnapshot } from '../../../../models/task.model';

/** Collapsible in-place console for a backend-owned project URL process. */
@Component({
  selector: 'app-project-url-process-console',
  standalone: true,
  imports: [PendingButtonDirective],
  templateUrl: './project-url-process-console.html',
  styleUrl: './project-url-process-console.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectUrlProcessConsoleComponent {
  readonly label = input.required<string>();
  readonly session = input<ProjectUrlProcessSnapshot | null>(null);
  readonly open = input(false);
  readonly stopping = input(false);
  readonly stopRequest = output<void>();
  readonly closeRequest = output<void>();

  readonly canStop = computed(() => {
    const state = this.session()?.state;
    return state === 'starting' || state === 'running';
  });

  statusLabel(state: ProjectUrlProcessSnapshot['state']): string {
    switch (state) {
      case 'starting': return 'Starting';
      case 'running': return 'Running';
      case 'stopped': return 'Stopped';
      case 'exited': return 'Exited';
      default: return 'Failed';
    }
  }
}
