import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TaskReferenceNavigationService } from '../../services/task-reference-navigation.service';
import { projectIdentity } from '../../services/project-identity.util';

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
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-reference-microcard.html',
  styleUrl: './task-reference-microcard.scss',
})
export class TaskReferenceMicrocardComponent {
  readonly status = input.required<TaskReferenceStatus>();
  private readonly navigation = inject(TaskReferenceNavigationService);

  readonly color = computed(() => this.status().projectColor || projectIdentity(this.status().projectName).color);
  readonly laneIcon = computed(() => lanePresentation(this.status().lane).icon);
  readonly laneLabel = computed(() => lanePresentation(this.status().lane).label);
  readonly laneTone = computed(() => lanePresentation(this.status().lane).tone);

  open(event: MouseEvent): void {
    event.preventDefault();
    this.navigation.openTaskKey(this.status().taskKey);
  }
}

function lanePresentation(lane: string | null): { icon: string; label: string; tone: string } {
  if (!lane) return { icon: '◇', label: 'Deleted or unknown task', tone: 'ghost' };
  if (lane === '6-completed' || lane === '7-archive') return { icon: '✓', label: lane === '7-archive' ? 'Archived' : 'Completed', tone: 'done' };
  if (lane === '3-progress' || lane === '4-auto-review') return { icon: '●', label: lane === '3-progress' ? 'In progress' : 'Post processing', tone: 'active' };
  if (lane === '5-human-review' || lane === '5e-escalated' || lane === '3b-code-not-complete') return { icon: '!', label: 'Waiting', tone: 'waiting' };
  return { icon: '○', label: lane === '2-ready' ? 'Ready' : 'Planned', tone: 'queued' };
}
