import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import type { EpicRollup } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { TooltipDirective } from '../../../../components/tooltip';

/**
 * Epic-membership banner: shown in the task-detail view when the open card is a
 * sub-task of an epic (epicId set, kind != epic). The inverse of
 * EpicRollupPaneComponent — instead of "where does my epic stand", this answers
 * "which epic does this task belong to, and take me back to it". Resolves the
 * epic's key + title via GET /api/epics/{id} (the only way TaskInfo's bare
 * epicId slug becomes human-readable) and emits `openEpic` so the host can open
 * the epic's detail. Renders nothing until the epic resolves, so an unknown or
 * deleted epicId leaves the header clean.
 */
@Component({
  selector: 'app-epic-membership-banner',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './epic-membership-banner.component.html',
  styleUrl: './epic-membership-banner.component.scss',
})
export class EpicMembershipBannerComponent {
  readonly epicId = input.required<string>();
  readonly watchPath = input<string>('');
  /** Click on the banner — host routes this to the epic's detail view. */
  readonly openEpic = output<{ jobId: string; watchPath: string }>();

  private readonly jobs = inject(TaskService);
  readonly epic = signal<EpicRollup | null>(null);

  constructor() {
    // Re-resolve whenever the bound epic changes (the lane pager swaps the
    // open sub-task for a peer that may sit under a different epic).
    effect(() => {
      const id = this.epicId();
      const wp = this.watchPath();
      if (!id) {
        this.epic.set(null);
        return;
      }
      this.jobs.getEpic(id, wp || undefined).subscribe({
        next: (r) => this.epic.set(r),
        error: () => this.epic.set(null),
      });
    });
  }
}
