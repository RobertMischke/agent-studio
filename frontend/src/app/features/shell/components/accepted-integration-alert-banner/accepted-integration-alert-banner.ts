import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';

interface AcceptedIntegrationAlertItem {
  taskKey: string;
  taskId: string;
  projectName: string;
  title: string;
  acceptedAt: string;
  integrationStatus: string;
  lastOutcome?: string | null;
  detail?: string | null;
}

interface AcceptedIntegrationAlertSnapshot {
  active: boolean;
  stalledTaskCount: number;
  thresholdMinutes: number;
  oldestAcceptedAt: string | null;
  observedAt: string;
  items: AcceptedIntegrationAlertItem[];
}

@Component({
  selector: 'app-accepted-integration-alert-banner',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './accepted-integration-alert-banner.html',
  styleUrl: './accepted-integration-alert-banner.scss',
})
export class AcceptedIntegrationAlertBannerComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  readonly projects = input<readonly string[]>([]);
  readonly snapshot = signal<AcceptedIntegrationAlertSnapshot | null>(null);
  readonly visibleItems = computed(() => {
    const snapshot = this.snapshot();
    if (!snapshot?.active) return [];
    const projects = this.projects();
    if (projects.length === 0) return snapshot.items;
    const visible = new Set(projects.map(project => project.toLowerCase()));
    return snapshot.items.filter(item => visible.has(item.projectName.toLowerCase()));
  });
  readonly thresholdMinutes = computed(() => this.snapshot()?.thresholdMinutes ?? 30);
  readonly projectTaskKeys = computed(() => {
    const groups = new Map<string, string[]>();
    for (const item of this.visibleItems()) {
      const keys = groups.get(item.projectName) ?? [];
      keys.push(item.taskKey);
      groups.set(item.projectName, keys);
    }
    return [...groups.entries()]
      .map(([project, keys]) => `${project}: ${keys.join(', ')}`)
      .join(' · ');
  });

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setInterval(() => this.refresh(), 15_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  private refresh(): void {
    this.http.get<AcceptedIntegrationAlertSnapshot>('/api/pipeline/accepted-integration-alert').subscribe({
      next: snapshot => this.snapshot.set(snapshot),
      error: () => this.snapshot.set(null),
    });
  }
}
