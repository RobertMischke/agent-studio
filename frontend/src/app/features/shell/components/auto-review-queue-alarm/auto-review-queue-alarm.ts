import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject } from '@angular/core';
import { NotificationComponent } from '../../../../components/notification/notification.component';
import { ReviewQueueTelemetryStore } from '../../../../services/review-queue-telemetry.store';

@Component({
  selector: 'app-auto-review-queue-alarm',
  standalone: true,
  imports: [NotificationComponent],
  templateUrl: './auto-review-queue-alarm.html',
  styleUrl: './auto-review-queue-alarm.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AutoReviewQueueAlarmComponent implements OnInit, OnDestroy {
  private readonly telemetry = inject(ReviewQueueTelemetryStore);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  readonly snapshot = this.telemetry.snapshot;

  ngOnInit(): void {
    this.telemetry.refresh();
    this.pollTimer = setInterval(() => this.telemetry.refresh(), 15_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }
}
