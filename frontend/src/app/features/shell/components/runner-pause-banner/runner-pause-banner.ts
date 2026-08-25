import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { NotificationComponent } from '../../../../components/notification/notification.component';
import type { ProjectRunnerStatus, RunnerStatus } from '../../../../models/task.model';

interface VisibleRunnerPause {
  projectName: string;
  reason: string;
  changedAt: string | null;
  autoResumeAt: string | null;
}

@Component({
  selector: 'app-runner-pause-banner',
  standalone: true,
  imports: [NotificationComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './runner-pause-banner.html',
  styleUrl: './runner-pause-banner.scss',
})
export class RunnerPauseBannerComponent implements OnInit, OnDestroy {
  private readonly http = inject(HttpClient);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  readonly projects = input<readonly string[]>([]);
  readonly status = signal<RunnerStatus>({ projects: {} });
  readonly pauses = computed<VisibleRunnerPause[]>(() => {
    const visible = new Set(this.projects().map(project => project.toLowerCase()));
    return Object.values(this.status().projects)
      .filter(project => visible.size === 0 || visible.has(project.projectName.toLowerCase()))
      .filter(project => this.isInfraPause(project))
      .map(project => ({
        projectName: project.projectName,
        reason: project.modeReason ?? project.breakerReason ?? 'infrastructure circuit breaker',
        changedAt: project.modeChangedAt ?? null,
        autoResumeAt: project.breakerCooldownUntil ?? null,
      }));
  });

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setInterval(() => this.refresh(), 15_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer) clearInterval(this.pollTimer);
  }

  time(value: string | null): string {
    if (!value) return 'time unknown';
    return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })
      .format(new Date(value));
  }

  private isInfraPause(project: ProjectRunnerStatus): boolean {
    if (project.mode !== 'manual' && project.mode !== 'paused') return false;
    const reason = `${project.modeReason ?? ''} ${project.breakerReason ?? ''}`.toLowerCase();
    return project.modeSource === 'circuit-breaker'
      || project.breakerState === 'cooldown'
      || reason.includes('infra breaker')
      || reason.includes('circuit-breaker');
  }

  private refresh(): void {
    this.http.get<RunnerStatus>('/api/runner/status').subscribe({
      next: status => this.status.set(status),
      error: () => this.status.set({ projects: {} }),
    });
  }
}
