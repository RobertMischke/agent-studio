import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  inject,
  input,
  signal,
} from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { AutoReviewStatusStore } from '../../../../services/auto-review-status.store';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { buildAutoReviewProcessBadge } from '../task-card/task-card-view-model';

const nowTick = signal(Date.now());
if (typeof window !== 'undefined') {
  setInterval(() => nowTick.set(Date.now()), 30_000);
}

@Component({
  selector: 'app-post-processing-activity',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './post-processing-activity.component.html',
  styleUrl: './post-processing-activity.component.scss',
})
export class PostProcessingActivityComponent implements OnInit, OnDestroy {
  readonly job = input.required<TaskInfo>();
  private readonly statusStore = inject(AutoReviewStatusStore);

  readonly activity = computed(() =>
    buildAutoReviewProcessBadge(this.job(), this.statusStore.status(), nowTick()));

  ngOnInit(): void {
    this.statusStore.subscribe();
  }

  ngOnDestroy(): void {
    this.statusStore.release();
  }
}
