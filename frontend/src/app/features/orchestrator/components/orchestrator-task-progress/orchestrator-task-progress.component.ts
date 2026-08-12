import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { TaskPlanView } from '../../../plan-strip/plan.model';

/** Compact, read-only projection of the active task agent's native plan. */
@Component({
  selector: 'app-orchestrator-task-progress',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-task-progress.component.html',
  styleUrl: './orchestrator-task-progress.component.scss',
})
export class OrchestratorTaskProgressComponent {
  readonly plan = input<TaskPlanView | null>(null);
  readonly isRunning = input(false);

  readonly visible = computed(() => !!this.plan()?.hasPlan && (this.plan()?.items.length ?? 0) > 0);
  readonly doneCount = computed(() => this.plan()?.items.filter(item => item.status === 'done').length ?? 0);
  readonly progressPercent = computed(() => {
    const total = this.plan()?.items.length ?? 0;
    return total === 0 ? 0 : Math.round(this.doneCount() * 100 / total);
  });

  glyph(status: string): string {
    if (status === 'done') return '☑';
    if (status === 'active') return '⟳';
    return '☐';
  }

  statusLabel(status: string): string {
    if (status === 'done') return 'complete';
    if (status === 'active') return 'active';
    return 'open';
  }
}
