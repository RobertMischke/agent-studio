import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { ProjectRunnerStatus, RemoteDispatchRejection, RunnerStatus } from '../../../../models/task.model';
import { NotificationComponent } from '../../../../components/notification/notification.component';

interface RemoteQueueStarvationItem {
  taskKey: string;
  taskId: string;
  projectName: string;
  title: string;
  cliType?: string | null;
  enteredLaneAt: string;
  lastRejection?: RemoteDispatchRejection | null;
  blockReasonCode?: string | null;
  blockReason?: string | null;
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
  readonly runnerStatus = signal<RunnerStatus | null>(null);
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
  readonly limitedCapabilities = computed(() => this.runnerStatus()?.capabilities ?? []);
  readonly limitedCliTypes = computed(() => new Set(
    this.limitedCapabilities().map(capability => capability.cliType.toLowerCase())));
  readonly stalledItems = computed(() => this.visibleItems().filter(item =>
    !item.cliType || !this.limitedCliTypes().has(item.cliType.toLowerCase())));
  readonly hasRejections = computed(() =>
    this.stalledItems().some(item => item.lastRejection != null));
  readonly buildProfileGateBlockedCount = computed(() =>
    this.stalledItems().filter(item => item.blockReasonCode === 'build-profile-gate').length);
  readonly breakerPauses = computed(() => {
    const projects = this.projects();
    const visible = projects.length === 0
      ? null
      : new Set(projects.map(project => project.toLowerCase()));
    return Object.values(this.runnerStatus()?.projects ?? {}).filter(status =>
      (status.mode === 'manual' || status.mode === 'paused')
      && status.modeSource === 'circuit-breaker'
      && (visible == null || visible.has(status.projectName.toLowerCase())));
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
    this.http.get<RunnerStatus>('/api/runner/status').subscribe({
      next: status => this.runnerStatus.set(status),
      error: () => this.runnerStatus.set(null),
    });
  }

  cliLabel(cliType: string): string {
    return cliType.length === 0 ? cliType : cliType[0].toUpperCase() + cliType.slice(1);
  }

  formatTime(value: string): string {
    return new Date(value).toLocaleString();
  }

  breakerHeadline(status: ProjectRunnerStatus): string {
    const count = status.breakerFailureCount ?? status.breakerTripCount ?? 0;
    const cli = status.breakerCliType ? ` cliType=${status.breakerCliType}` : '';
    const at = status.modeChangedAt ? ` at ${this.formatTime(status.modeChangedAt)}` : '';
    return `Pickup paused: infra breaker, ${count} failures${cli}${at}.`;
  }
}
