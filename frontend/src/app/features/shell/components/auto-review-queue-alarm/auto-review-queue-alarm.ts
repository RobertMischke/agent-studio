import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject } from '@angular/core';
import { NotificationComponent } from '../../../../components/notification/notification.component';
import { AutoReviewQueueTelemetryStore } from '../../../../services/auto-review-queue-telemetry.store';

@Component({
  selector: 'app-auto-review-queue-alarm',
  standalone: true,
  imports: [NotificationComponent],
  templateUrl: './auto-review-queue-alarm.html',
  styleUrl: './auto-review-queue-alarm.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AutoReviewQueueAlarmComponent implements OnInit, OnDestroy {
  private readonly store = inject(AutoReviewQueueTelemetryStore);

  readonly snapshot = this.store.status;
  readonly visible = computed(() => this.snapshot()?.isStagnant === true);

  ngOnInit(): void { this.store.subscribe(); }
  ngOnDestroy(): void { this.store.release(); }
}
