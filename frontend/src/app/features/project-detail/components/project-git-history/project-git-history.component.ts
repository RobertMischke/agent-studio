import { ChangeDetectionStrategy, Component, computed, inject, input, output } from '@angular/core';
import { TaskReferenceNavigationService } from '../../../../services/task-reference-navigation.service';
import { formatCompactDateTime } from '../../../../services/format.util';
import {
  buildGitCommitChips,
  buildGitGraphRows,
  type GitGraphCommit,
  type GitTaskBadge,
} from '../../../git';

@Component({
  selector: 'app-project-git-history',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-git-history.component.html',
  styleUrl: './project-git-history.component.scss',
})
export class ProjectGitHistoryComponent {
  private readonly taskNavigation = inject(TaskReferenceNavigationService);

  readonly commits = input.required<readonly GitGraphCommit[]>();
  readonly selectedSha = input<string | null>(null);
  readonly hasMore = input(false);
  readonly loadingMore = input(false);

  readonly commitSelected = output<GitGraphCommit>();
  readonly changesRequested = output<GitGraphCommit>();
  readonly loadMore = output<void>();

  readonly rows = computed(() => buildGitGraphRows(this.commits()).map(row => ({
    ...row,
    chips: buildGitCommitChips(row.commit),
  })));

  when(value: string): string {
    return formatCompactDateTime(value);
  }

  openTask(event: MouseEvent, task: GitTaskBadge): void {
    event.stopPropagation();
    this.taskNavigation.openTaskKey(task.taskKey);
  }
}
