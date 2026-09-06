import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TaskReferenceNavigationService } from '../../services/task-reference-navigation.service';
import { projectIdentity } from '../../services/project-identity.util';
import { AppTooltipDirective } from '../tooltip/app-tooltip.directive';
import { lanePresentation, laneToneValue } from '../../models/lane-presentation';

export interface TaskReferenceMergeStatus {
  inIntegration: boolean;
  inRelease: boolean;
  integrationBranch: string;
  releaseBranch: string;
}

export interface TaskReferenceStatus {
  key: string;
  exists: boolean;
  taskKey: string | null;
  title: string | null;
  lane: string | null;
  projectId: string;
  projectName: string;
  projectColor: string | null;
  merge: TaskReferenceMergeStatus | null;
  reviewGrade: string | null;
}

@Component({
  selector: 'app-task-reference-microcard',
  standalone: true,
  imports: [AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-reference-microcard.html',
  styleUrl: './task-reference-microcard.scss',
})
export class TaskReferenceMicrocardComponent {
  readonly status = input.required<TaskReferenceStatus>();
  /** Show key, title, and lane together when the reference is the primary receipt. */
  readonly expanded = input(false);
  readonly variant = input<'default' | 'lane-dot'>('default');
  readonly testId = input('task-reference-microcard');
  private readonly navigation = inject(TaskReferenceNavigationService);

  readonly color = computed(
    () => this.status().projectColor || projectIdentity(this.status().projectName).color,
  );
  readonly lane = computed(() => lanePresentation(this.status().lane));
  readonly laneIcon = computed(() => this.lane()?.glyph ?? '◇');
  readonly laneLabel = computed(() => this.lane()?.shortName ?? 'Deleted or unknown task');
  readonly laneTone = computed(() => this.lane()?.toneToken ?? null);
  readonly laneToneValue = computed(() => laneToneValue(this.status().lane));
  readonly mergeLabel = computed(() => {
    const merge = this.status().merge;
    if (!merge) return null;
    return `${merge.integrationBranch} ${merge.inIntegration ? 'merged' : 'not merged'}, ${merge.releaseBranch} ${merge.inRelease ? 'merged' : 'not merged'}`;
  });
  readonly mergePopoverLabel = computed(() => {
    const merge = this.status().merge;
    if (!merge) return null;
    const integrationStatus = merge.inIntegration ? 'merged' : 'open';
    const releaseStatus = merge.inRelease ? 'merged' : 'open';
    return `${merge.integrationBranch}: ${integrationStatus} · ${merge.releaseBranch}: ${releaseStatus}`;
  });
  readonly tooltipLabel = computed(() => [
    this.status().title || 'Unknown or deleted task',
    `${this.laneLabel()} · ${this.status().projectName}`,
    this.mergePopoverLabel(),
    this.status().reviewGrade ? `Review grade ${this.status().reviewGrade}` : null,
  ].filter(Boolean).join('\n'));

  open(event: MouseEvent): void {
    event.preventDefault();
    this.navigation.openTaskKey(this.status().taskKey);
  }
}
