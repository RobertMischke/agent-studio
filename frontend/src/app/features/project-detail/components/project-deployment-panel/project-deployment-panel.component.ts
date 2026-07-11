import { ChangeDetectionStrategy, Component, effect, inject, input, signal } from '@angular/core';
import { DatePipe, DecimalPipe } from '@angular/common';
import { TaskService } from '../../../../services/task.service';
import type { ProjectDeploymentSummary } from '../../../../models/project-overview.model';

@Component({
  selector: 'app-project-deployment-panel',
  standalone: true,
  imports: [DatePipe, DecimalPipe],
  templateUrl: './project-deployment-panel.component.html',
  styleUrl: './project-deployment-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectDeploymentPanelComponent {
  private readonly tasks = inject(TaskService);

  readonly projectName = input.required<string>();
  readonly summary = signal<ProjectDeploymentSummary | null>(null);
  readonly loading = signal(true);
  readonly requestFailed = signal(false);

  constructor() {
    effect(() => this.load(this.projectName()));
  }

  refresh(): void {
    this.load(this.projectName());
  }

  shortSha(sha: string): string {
    return sha.slice(0, 8);
  }

  private load(projectName: string): void {
    this.loading.set(true);
    this.requestFailed.set(false);
    this.tasks.getProjectDeploymentSummary(projectName).subscribe({
      next: summary => {
        this.summary.set(summary);
        this.loading.set(false);
      },
      error: () => {
        this.summary.set(null);
        this.requestFailed.set(true);
        this.loading.set(false);
      },
    });
  }
}
