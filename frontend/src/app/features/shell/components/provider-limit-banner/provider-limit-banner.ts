import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { NotificationComponent } from '../../../../components/notification/notification.component';
import type { RunnerStatus } from '../../../../models/task.model';

@Component({
  selector: 'app-provider-limit-banner',
  standalone: true,
  imports: [NotificationComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './provider-limit-banner.html',
})
export class ProviderLimitBannerComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  readonly limits = signal<NonNullable<RunnerStatus['providerLimits']>>([]);
  readonly breakerPauses = signal<string[]>([]);
  readonly active = computed(() => this.limits().length > 0 || this.breakerPauses().length > 0);
  readonly summary = computed(() => [
    ...this.limits().map(limit => `${limit.provider}: limited until ${this.format(limit.retryAt)}`),
    ...this.breakerPauses(),
  ].join('; '));

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setInterval(() => this.refresh(), 15_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  private refresh(): void {
    this.http.get<RunnerStatus>('/api/runner/status').subscribe({
      next: status => {
        this.limits.set(status.providerLimits ?? []);
        this.breakerPauses.set(Object.values(status.projects)
          .filter(project => project.modeSource === 'circuit-breaker')
          .map(project => `${project.projectName}: ${project.modeReason ?? 'pickup paused: infra breaker'}`));
      },
      error: () => {
        this.limits.set([]);
        this.breakerPauses.set([]);
      },
    });
  }

  private format(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
  }
}
