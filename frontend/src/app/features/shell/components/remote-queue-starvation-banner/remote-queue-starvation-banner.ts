import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import type { RemoteDispatchRejection } from '../../../../models/task.model';

interface RemoteQueueStarvationItem {
  taskKey: string;
  taskId: string;
  projectName: string;
  title: string;
  enteredLaneAt: string;
  lastRejection?: RemoteDispatchRejection | null;
}

interface RemoteQueueStarvationSnapshot {
  active: boolean;
  waitingTaskCount: number;
  availableSlots: number;
  thresholdMinutes: number;
  oldestEnteredLaneAt: string | null;
  observedAt: string;
  items: RemoteQueueStarvationItem[];
}

@Component({
  selector: 'app-remote-queue-starvation-banner',
  standalone: true,
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
