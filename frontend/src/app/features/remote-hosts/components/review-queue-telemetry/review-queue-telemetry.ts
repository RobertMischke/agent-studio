import { DatePipe, DecimalPipe } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import type { AutoReviewQueueTelemetrySnapshot } from '../../../../services/auto-review-queue-telemetry.store';

@Component({
  selector: 'app-review-queue-telemetry',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  templateUrl: './review-queue-telemetry.html',
  styleUrl: './review-queue-telemetry.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ReviewQueueTelemetryComponent {
  readonly snapshot = input<AutoReviewQueueTelemetrySnapshot | null>(null);
  readonly unavailable = input(false);

  readonly stateLabel = computed(() => {
    const queue = this.snapshot();
    if (!queue) return null;
    if (queue.isStagnant) return 'Stagnant';
    if (queue.queueDepth === 0) return 'Clear';
    return queue.drainRatePerHour > 0 ? 'Draining' : 'Waiting';
  });

  readonly medianLabel = computed(() => {
    const seconds = this.snapshot()?.medianReviewDurationSeconds;
    if (seconds === null || seconds === undefined) return 'No sample';
    return `${new Intl.NumberFormat('en', {
      minimumFractionDigits: 0,
      maximumFractionDigits: 1,
    }).format(seconds / 60)} min`;
  });
}
