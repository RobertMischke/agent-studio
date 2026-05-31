import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { buildEpicGroups, EpicGroupView } from '../epic-grouping.util';
import { TaskCardComponent } from '../task-card/task-card.component';
import { TooltipDirective } from '../../../../components/tooltip';

/**
 * Group-by-epic board view: the "Gruppieren nach Epic" toggle swaps the lane
 * columns for this tree. Each epic is a section (the epic card plus its
 * sub-tasks) with a live "completed / total" rollup that mirrors the backend
 * `GET /api/epics`. Ordinary tasks with no epic and orphaned sub-tasks get
 * their own synthetic sections via `buildEpicGroups`.
 *
 * Read-only and additive: it reuses `<app-job-card>` for every card so the
 * EPIC badge, sub-task chip, drag affordance, and context-menu epic assignment
 * (way 2) all keep working unchanged. Clicking a card opens its detail, same as
 * the lane view.
 */
@Component({
  selector: 'app-epic-group-board',
  standalone: true,
  imports: [TaskCardComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './epic-group-board.component.html',
  styleUrl: './epic-group-board.component.scss',
})
export class EpicGroupBoardComponent {
  readonly tasks = input<readonly TaskInfo[]>([]);
  readonly compact = input<boolean>(false);
  readonly highlightJobId = input<string | null>(null);
  readonly jobClick = output<TaskInfo>();

  readonly groups = computed<EpicGroupView[]>(() => buildEpicGroups(this.tasks()));

  /** Ids of collapsed sections. Local view state; not persisted. */
  private readonly collapsed = signal<ReadonlySet<string>>(new Set());

  isCollapsed(id: string): boolean {
    return this.collapsed().has(id);
  }

  toggleCollapse(id: string): void {
    const next = new Set(this.collapsed());
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.collapsed.set(next);
  }

  /** Header glyph: epic puzzle piece, a folder for "No epic", a warning for orphans. */
  groupIcon(group: EpicGroupView): string {
    if (group.epic) return '🧩';
    return group.id === '__orphan__' ? '⚠️' : '🗂️';
  }
}
