import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import type { HttpErrorResponse } from '@angular/common/http';
import { ProjectCycleTimeService } from '../../services/project-cycle-time.service';
import type { TaskLaneTransition } from '../../models/project-cycle-time.model';
import { formatDuration, formatTimestamp, laneLabel } from '../project-cycle-time-panel/cycle-time-format';

/**
 * Inline transition history of one task (drill-down row of the Cycle Time
 * panel). Fetches the per-task endpoint on mount so the list payload stays
 * bounded; every lane change is one row with dwell, actor, cause, and detail.
 */
@Component({
  selector: 'app-cycle-time-task-transitions',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './cycle-time-task-transitions.component.html',
  styleUrl: './cycle-time-task-transitions.component.scss',
})
export class CycleTimeTaskTransitionsComponent {
  private readonly api = inject(ProjectCycleTimeService);

  readonly projectName = input.required<string>();
  readonly taskKey = input.required<string>();

  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);
  readonly transitions = signal<TaskLaneTransition[]>([]);

  constructor() {
    effect(() => {
      const project = this.projectName();
      const key = this.taskKey();
      if (project && key) this.load(project, key);
    });
  }

  laneLabel(lane: string): string {
    return laneLabel(lane);
  }

  time(iso: string): string {
    return formatTimestamp(iso);
  }

  duration(seconds: number | null | undefined): string {
    return formatDuration(seconds) ?? '–';
  }

  causeLabel(cause: string): string {
    return cause.replace(/-/g, ' ');
  }

  private load(project: string, key: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.api.loadTask(project, key).subscribe({
      next: response => {
        this.transitions.set(response.task.transitions ?? []);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.transitions.set([]);
        this.error.set(err?.error?.error ?? err?.message ?? 'Could not load transitions.');
        this.loading.set(false);
      },
    });
  }
}
