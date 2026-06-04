import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import type { EpicRollup, EpicSubTaskRef } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { TooltipDirective } from '../../../../components/tooltip';
import { LANE_LABELS } from '../../state/lane-pager.service';

/** One lane column in the epic mini-board: a state plus the sub-tasks that sit in it. */
export interface EpicLaneGroup {
  state: string;
  label: string;
  subTasks: EpicSubTaskRef[];
}

/** Canonical kanban lane order; `LANE_LABELS` is authored in that order. */
const LANE_ORDER = Object.keys(LANE_LABELS);

/**
 * Epic rollup pane: shown in the task-detail view when the open card is an
 * epic (kind=epic). Renders the live sub-task progress from
 * GET /api/epics/{id} as a full-width mini-board: the sub-tasks are grouped
 * into the lane/state columns they currently sit in, so a glance shows where
 * the epic's work stands. Read-only apart from navigation - assignment happens
 * on the cards (way 2) or in the create dialog (way 1); clicking a sub-task
 * opens its detail.
 */
@Component({
  selector: 'app-epic-rollup-pane',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './epic-rollup-pane.component.html',
  styleUrl: './epic-rollup-pane.component.scss',
})
export class EpicRollupPaneComponent {
  readonly epicId = input.required<string>();
  readonly watchPath = input<string>('');

  /** Bubbles a click on a sub-task so the host opens its detail. */
  readonly openSubTask = output<{ jobId: string; watchPath: string }>();

  private readonly jobs = inject(TaskService);
  readonly rollup = signal<EpicRollup | null>(null);
  readonly loading = signal(false);
  readonly error = signal(false);

  /** Completed share of the epic, 0-100, for the progress bar width. */
  readonly progressPct = computed(() => {
    const r = this.rollup();
    if (!r || r.subTaskTotal === 0) return 0;
    return Math.round((r.completed / r.subTaskTotal) * 100);
  });

  /**
   * Sub-tasks bucketed into the lanes they currently sit in, in kanban order.
   * Empty lanes are dropped so the board only shows lanes that hold work;
   * unknown/legacy states are appended after the canonical ones.
   */
  readonly laneGroups = computed<EpicLaneGroup[]>(() => {
    const r = this.rollup();
    if (!r) return [];
    const byState = new Map<string, EpicSubTaskRef[]>();
    for (const sub of r.subTasks) {
      const bucket = byState.get(sub.state);
      if (bucket) bucket.push(sub);
      else byState.set(sub.state, [sub]);
    }
    const known = LANE_ORDER.filter((s) => byState.has(s));
    const unknown = [...byState.keys()].filter((s) => !LANE_ORDER.includes(s)).sort();
    return [...known, ...unknown].map((state) => ({
      state,
      label: LANE_LABELS[state] ?? this.laneLabel(state),
      subTasks: [...byState.get(state)!].sort((a, b) => a.order - b.order),
    }));
  });

  constructor() {
    // Re-fetch whenever the bound epic changes (lane pager swaps the open card).
    effect(() => {
      const id = this.epicId();
      const wp = this.watchPath();
      if (!id) return;
      this.loading.set(true);
      this.error.set(false);
      this.jobs.getEpic(id, wp || undefined).subscribe({
        next: (r) => { this.rollup.set(r); this.loading.set(false); },
        error: () => { this.error.set(true); this.loading.set(false); },
      });
    });
  }

  /** "6-completed" -> "completed" for an unknown lane label fallback. */
  laneLabel(state: string): string {
    const name = state.includes('-') ? state.substring(state.indexOf('-') + 1) : state;
    return name.replace(/-/g, ' ');
  }

  openSub(sub: EpicSubTaskRef): void {
    this.openSubTask.emit({ jobId: sub.id, watchPath: this.watchPath() });
  }

  trackByLane = (_: number, lane: EpicLaneGroup) => lane.state;
}
