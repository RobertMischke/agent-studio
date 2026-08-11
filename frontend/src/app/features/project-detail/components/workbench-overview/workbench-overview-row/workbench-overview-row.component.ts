import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../../../components/task-reference-microcard/task-reference-microcard';
import { deriveProjectShortCode } from '../../../../../models/project-basics.model';
import type { WorkbenchOverviewItem } from '../../../../../models/project-docs.model';
import { projectIdentity } from '../../../../../services/project-identity.util';
import { WorkbenchViewerComponent } from '../../workbench-viewer/workbench-viewer.component';

export type WorkbenchOverviewRowKind = 'decision' | 'current' | 'invalid' | 'history';

@Component({
  selector: 'app-workbench-overview-row',
  standalone: true,
  imports: [TaskReferenceMicrocardComponent, WorkbenchViewerComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-overview-row.component.html',
  styleUrl: './workbench-overview-row.component.scss',
})
export class WorkbenchOverviewRowComponent {
  readonly item = input.required<WorkbenchOverviewItem>();
  readonly kind = input.required<WorkbenchOverviewRowKind>();
  readonly expanded = input(false);
  readonly referenceKeys = input<readonly string[]>([]);
  readonly taskStatuses = input<readonly TaskReferenceStatus[]>([]);
  readonly taskStatusesLoading = input(false);
  readonly openItem = output<WorkbenchOverviewItem>();
  readonly toggleReview = output<WorkbenchOverviewItem>();

  projectShortCode(): string {
    const item = this.item();
    return item.projectShortCode?.trim().toUpperCase()
      || deriveProjectShortCode(item.projectName)
      || projectIdentity(item.projectName).initial;
  }

  projectColor(): string {
    const item = this.item();
    return item.projectColor?.trim() || projectIdentity(item.projectName).color;
  }

  statusLabel(): string {
    const workbench = this.item().workbench;
    if (!workbench.valid) return 'Needs attention';
    if (workbench.status === 'documented') return 'Documented';
    if (workbench.status === 'archived') return 'Discarded';
    if (workbench.status === 'decision-pending') return 'Decision pending';
    if (workbench.status === 'active') return humanize(workbench.phase ?? 'active');
    if (workbench.status === 'decided') {
      return workbench.documentation?.eligible ? 'Ready to document' : 'Accepted / In progress';
    }
    if (workbench.documentation?.eligible) return 'Ready to document';
    return humanize(workbench.status);
  }

  openDecisionLabel(): string {
    const workbench = this.item().workbench;
    const count = workbench.openDecisionCount
      ?? (workbench.status === 'decision-pending' ? 1 : 0);
    return `${count} open ${count === 1 ? 'decision' : 'decisions'}`;
  }

  updatedLabel(): string {
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(this.item().workbench.updatedAtUtc));
  }
}

function humanize(value: string): string {
  const words = value.replaceAll('-', ' ');
  return words.charAt(0).toUpperCase() + words.slice(1);
}
