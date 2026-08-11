import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TaskReferenceNavigationService } from '../../services/task-reference-navigation.service';
import { projectIdentity } from '../../services/project-identity.util';
import { AppTooltipDirective } from '../tooltip/app-tooltip.directive';

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
  readonly laneIcon = computed(() => lanePresentation(this.status().lane).icon);
  readonly laneLabel = computed(() => lanePresentation(this.status().lane).label);
  readonly laneTone = computed(() => lanePresentation(this.status().lane).tone);
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

function lanePresentation(lane: string | null): { icon: string; label: string; tone: string } {
  if (!lane) return { icon: '◇', label: 'Deleted or unknown task', tone: 'ghost' };
  if (lane === '6-completed' || lane === '7-archive')
    return { icon: '✓', label: lane === '7-archive' ? 'Archived' : 'Completed', tone: 'done' };
  if (lane === '3-progress' || lane === '4-auto-review')
    return {
      icon: '●',
      label: lane === '3-progress' ? 'In progress' : 'Post processing',
      tone: 'active',
    };
  if (lane === '5-human-review' || lane === '5e-escalated' || lane === '3b-code-not-complete')
    return { icon: '!', label: 'Waiting', tone: 'waiting' };
  if (lane === '0-backlog') return { icon: '○', label: 'Backlog', tone: 'queued' };
  if (lane === '1-preparation') return { icon: '○', label: 'Preparation', tone: 'queued' };
  if (lane === '2-ready') return { icon: '○', label: 'Ready', tone: 'queued' };
  return { icon: '○', label: 'Planned', tone: 'queued' };
}
