import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import type { EpicRollup } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { TooltipDirective } from '../../../../components/tooltip';

/**
 * Epic rollup pane: shown in the task-detail view when the open card is an
 * epic (kind=epic). Renders the live sub-task progress + list from
 * GET /api/epics/{id}. Read-only - assignment happens on the cards (way 2) or
 * in the create dialog (way 1); this pane is the "where does my epic stand"
 * surface the user asked for ("eigene Board-Sicht" for an epic, in miniature).
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

  /** "6-completed" -> "completed" for the sub-task lane label. */
  laneLabel(state: string): string {
    const name = state.includes('-') ? state.substring(state.indexOf('-') + 1) : state;
    return name.replace(/-/g, ' ');
  }
}
