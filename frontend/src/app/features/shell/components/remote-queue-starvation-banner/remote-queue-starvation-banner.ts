import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { RemoteDispatchRejection } from '../../../../models/task.model';
import { NotificationComponent } from '../../../../components/notification/notification.component';

interface RemoteQueueStarvationItem {
  taskKey: string;
  taskId: string;
  projectName: string;
  title: string;
  enteredLaneAt: string;
  lastRejection?: RemoteDispatchRejection | null;
}

/** One CLI whose shared provider account is out of budget until `resetAt`. */
interface RemoteQueueProviderLimit {
  cliType: string;
  resetAt: string;
  waitingTaskCount: number;
  reason: string;
}

interface RemoteQueueStarvationSnapshot {
  active: boolean;
  waitingTaskCount: number;
  availableSlots: number;
  thresholdMinutes: number;
  claimProgressStalled: boolean;
  lastSuccessfulClaimAt: string | null;
  hasRejections: boolean;
  oldestEnteredLaneAt: string | null;
  observedAt: string;
  items: RemoteQueueStarvationItem[];
  providerLimits?: RemoteQueueProviderLimit[] | null;
}

@Component({
  selector: 'app-remote-queue-starvation-banner',
  standalone: true,
  imports: [NotificationComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './remote-queue-starvation-banner.html',
  styleUrl: './remote-queue-starvation-banner.scss',
})
export class RemoteQueueStarvationBannerComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  readonly projects = input<readonly string[]>([]);
  readonly snapshot = signal<RemoteQueueStarvationSnapshot | null>(null);
  readonly visibleItems = computed(() => {
    const snapshot = this.snapshot();
    if (!snapshot?.active) return [];
    const projects = this.projects();
    if (projects.length === 0) return snapshot.items;
    const visible = new Set(projects.map(project => project.toLowerCase()));
    return snapshot.items.filter(item => visible.has(item.projectName.toLowerCase()));
  });
  readonly availableSlots = computed(() => this.snapshot()?.availableSlots ?? 0);
  readonly thresholdMinutes = computed(() => this.snapshot()?.thresholdMinutes ?? 0);
  readonly hasRejections = computed(() =>
    this.visibleItems().some(item => item.lastRejection != null));

  /**
   * A parked provider account is a different fact from a stalled queue: nothing
   * is broken, no operator action helps, and the cards resume by themselves.
   * The banner therefore names the limit and its reset instead of reporting
   * free capacity nobody can use.
   */
  readonly providerLimits = computed(() => this.snapshot()?.providerLimits ?? []);
  readonly providerLimited = computed(() => this.providerLimits().length > 0);
  readonly limitedClis = computed(() =>
    this.providerLimits().map(limit => limit.cliType).join(', '));
  readonly limitedTaskCount = computed(() =>
    this.providerLimits().reduce((total, limit) => total + limit.waitingTaskCount, 0));

  /** The soonest reset across all parked CLIs, as a local wall-clock time. */
  readonly limitResetLabel = computed(() => {
    const resets = this.providerLimits()
      .map(limit => Date.parse(limit.resetAt))
      .filter(value => !Number.isNaN(value));
    if (resets.length === 0) return '';
    return new Date(Math.min(...resets)).toLocaleTimeString([], {
      hour: '2-digit',
      minute: '2-digit',
    });
  });

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setInterval(() => this.refresh(), 15_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  private refresh(): void {
    this.http.get<RemoteQueueStarvationSnapshot>('/api/runner/queue-starvation').subscribe({
      next: snapshot => this.snapshot.set(snapshot),
      error: () => this.snapshot.set(null),
    });
  }
}
