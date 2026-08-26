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
  blockReasonCode?: string | null;
  blockReason?: string | null;
}

interface PickupPauseItem {
  projectName: string;
  reason: string;
  pausedAt: string | null;
}

interface RemoteQueueStarvationSnapshot {
  active: boolean;
  waitingTaskCount: number;
  availableSlots: number;
  thresholdMinutes: number;
  claimProgressStalled: boolean;
  lastSuccessfulClaimAt: string | null;
  hasRejections: boolean;
  buildProfileGateBlockedTaskCount: number;
  providerLimitedTaskCount: number;
  state: 'healthy' | 'limited' | 'paused' | 'stalled';
  providerLimitReason: string | null;
  pickupPausedProjectCount: number;
  pickupPauses: PickupPauseItem[];
  oldestEnteredLaneAt: string | null;
  observedAt: string;
  items: RemoteQueueStarvationItem[];
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
  readonly visiblePickupPauses = computed(() => {
    const pauses = this.snapshot()?.pickupPauses ?? [];
    const projects = this.projects();
    if (projects.length === 0) return pauses;
    const visible = new Set(projects.map(project => project.toLowerCase()));
    return pauses.filter(item => visible.has(item.projectName.toLowerCase()));
  });
  readonly availableSlots = computed(() => this.snapshot()?.availableSlots ?? 0);
  readonly thresholdMinutes = computed(() => this.snapshot()?.thresholdMinutes ?? 0);
  readonly hasRejections = computed(() =>
    this.visibleItems().some(item => item.lastRejection != null));
  readonly buildProfileGateBlockedCount = computed(() =>
    this.visibleItems().filter(item => item.blockReasonCode === 'build-profile-gate').length);
  readonly providerLimitedCount = computed(() =>
    this.visibleItems().filter(item => item.blockReasonCode === 'provider-limited').length);
  readonly providerLimitReason = computed(() =>
    this.visibleItems().find(item => item.blockReasonCode === 'provider-limited')?.blockReason
      ?? this.snapshot()?.providerLimitReason
      ?? 'Provider recovery is pending.');
  readonly limitedProvider = computed(() => {
    const reason = this.providerLimitReason();
    const match = reason.match(/provider-auth:([a-z0-9_-]+)/i)
      ?? reason.match(/^([a-z0-9_-]+):\s*limited until/i);
    const provider = match?.[1] ?? 'provider';
    return provider.charAt(0).toUpperCase() + provider.slice(1);
  });
  readonly pickupPauseSummary = computed(() => {
    const pauses = this.visiblePickupPauses();
    if (pauses.length === 0) return '';
    return pauses.map(pause => `${pause.projectName}: ${pause.reason}`).join(' ');
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
