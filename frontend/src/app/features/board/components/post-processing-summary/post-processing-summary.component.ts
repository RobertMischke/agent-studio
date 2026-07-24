import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  input,
} from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { AutoReviewStatusStore } from '../../../../services/auto-review-status.store';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { buildAutoReviewProcessBadge } from '../task-card/task-card-view-model';

@Component({
  selector: 'app-post-processing-summary',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './post-processing-summary.component.html',
  styleUrl: './post-processing-summary.component.scss',
})
export class PostProcessingSummaryComponent implements OnInit, OnDestroy {
  readonly jobs = input.required<readonly TaskInfo[]>();
  readonly nowMs = input<number>(0);
  private readonly statusStore = inject(AutoReviewStatusStore);

  readonly summary = computed(() => {
    let active = 0;
    let waiting = 0;
    const now = this.nowMs() || Date.now();
    for (const job of this.jobs()) {
      const activity = buildAutoReviewProcessBadge(job, this.statusStore.status(), now);
      if (activity?.tone === 'active') active++;
      else waiting++;
    }
    return {
      active,
      waiting,
      tooltip: `${active} active post-processing task${active === 1 ? '' : 's'}, `
        + `${waiting} waiting. Gate-queued tasks count as waiting.`,
    };
  });

  ngOnInit(): void {
    this.statusStore.subscribe();
  }

  ngOnDestroy(): void {
    this.statusStore.release();
  }
}
