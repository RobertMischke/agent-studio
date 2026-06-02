import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  OnInit,
  output,
  signal,
} from '@angular/core';
import { TooltipDirective } from '../../../../components/tooltip';
import { TaskService } from '../../../../services/task.service';
import { projectIdentity } from '../../../../services/project-identity.util';
import type { EpicRollup } from '../../../../models/task.model';

/**
 * Dedicated read-only epic overview at `#/epics`. Lists every epic from
 * `GET /api/epics` with a segmented done / in-progress / open progress bar
 * and an expandable sub-task list. The board already offers assignment
 * (create dialog way 1, card context menu way 2); this screen is purely the
 * "where do my epics stand" surface, so the only mutation it surfaces is
 * navigation: clicking an epic or a sub-task opens that card's detail.
 *
 * Mirrors {@link BacklogTriageScreenComponent}: the host owns the route
 * (`EpicOverviewService`) and the detail-open flow; this component fetches,
 * renders, and emits.
 */
@Component({
  selector: 'app-epic-overview-screen',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './epic-overview-screen.component.html',
  styleUrl: './epic-overview-screen.component.scss',
})
export class EpicOverviewScreenComponent implements OnInit {
  private readonly jobs = inject(TaskService);

  readonly closeRequested = output<void>();
  /** Bubbles a click on an epic or sub-task so the host opens its detail. */
  readonly openTask = output<{ jobId: string; watchPath: string }>();

  readonly epics = signal<EpicRollup[]>([]);
  readonly loading = signal(false);
  readonly error = signal(false);
  /** Epic ids whose sub-task list is currently expanded. */
  readonly expanded = signal<ReadonlySet<string>>(new Set());

  readonly totalEpics = computed(() => this.epics().length);

  ngOnInit(): void {
    this.loading.set(true);
    this.error.set(false);
    this.jobs.getEpics().subscribe({
      next: (list) => {
        this.epics.set(list ?? []);
        this.loading.set(false);
      },
      error: () => {
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }

  isExpanded(epicId: string): boolean {
    return this.expanded().has(epicId);
  }

  toggleExpanded(epicId: string): void {
    const next = new Set(this.expanded());
    if (next.has(epicId)) next.delete(epicId); else next.add(epicId);
    this.expanded.set(next);
  }

  /** Width % of a segment within the progress bar; 0 total renders empty. */
  segmentPct(count: number, total: number): number {
    if (total <= 0) return 0;
    return (count / total) * 100;
  }

  identityFor(name: string) {
    return projectIdentity(name);
  }

  /** "6-completed" -> "completed" for the sub-task lane label. */
  laneLabel(state: string): string {
    const name = state.includes('-') ? state.substring(state.indexOf('-') + 1) : state;
    return name.replace(/-/g, ' ');
  }

  openEpic(epic: EpicRollup): void {
    this.openTask.emit({ jobId: epic.id, watchPath: epic.watchPath });
  }

  openSubTask(epic: EpicRollup, subId: string): void {
    this.openTask.emit({ jobId: subId, watchPath: epic.watchPath });
  }

  close(): void {
    this.closeRequested.emit();
  }

  trackByEpic = (_: number, epic: EpicRollup) => epic.id;
}
