import { ChangeDetectionStrategy, Component, computed, effect, inject, input, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { finalize } from 'rxjs';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { TaskService } from '../../../../services/task.service';

export interface ProjectProposal {
  id: string;
  generation: string;
  finding: string;
  evidenceScreenshot: string;
  proposal: string;
  estimatedEffort: string;
  severity: 'critical' | 'medium' | 'low';
  status: 'proposed' | 'approved' | 'rejected' | 'spawned';
  spawnedTask: string | null;
  relPath: string;
  updatedAt: string;
}

@Component({
  selector: 'app-project-proposals-panel',
  standalone: true,
  imports: [TaskReferenceMicrocardComponent],
  templateUrl: './project-proposals-panel.component.html',
  styleUrl: './project-proposals-panel.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectProposalsPanelComponent {
  private readonly http = inject(HttpClient);
  private readonly tasks = inject(TaskService);
  readonly projectName = input.required<string>();
  readonly items = signal<ProjectProposal[]>([]);
  readonly selectedId = signal<string | null>(null);
  readonly loading = signal(false);
  readonly deciding = signal(false);
  readonly error = signal<string | null>(null);
  readonly taskStatuses = signal<ReadonlyMap<string, TaskReferenceStatus>>(new Map());
  readonly selected = computed(() => this.items().find(p => p.id === this.selectedId()) ?? null);
  readonly proposedCount = computed(() => this.items().filter(p => p.status === 'proposed').length);
  readonly generations = computed(() => new Set(this.items().map(p => p.generation)).size);

  constructor() {
    effect(() => {
      const project = this.projectName();
      if (project) this.load(project);
    });
  }

  select(id: string): void { this.selectedId.set(id); }

  evidenceUrl(proposal: ProjectProposal): string {
    const path = proposal.evidenceScreenshot.replace(/^\.\//, '');
    return `/api/projects/${encodeURIComponent(this.projectName())}/proposals/evidence/${path.split('/').map(encodeURIComponent).join('/')}`;
  }

  decide(decision: 'approve' | 'reject'): void {
    const proposal = this.selected();
    if (!proposal || this.deciding()) return;
    this.deciding.set(true);
    this.error.set(null);
    this.http.post<{ proposal: ProjectProposal }>(
      `/api/projects/${encodeURIComponent(this.projectName())}/proposals/${encodeURIComponent(proposal.id)}/decision`,
      { decision },
    ).pipe(finalize(() => this.deciding.set(false))).subscribe({
      next: result => {
        this.items.update(items => items.map(item => item.id === result.proposal.id ? result.proposal : item));
        this.hydrateTasks();
      },
      error: () => this.error.set(`Could not ${decision} this proposal.`),
    });
  }

  taskStatus(key: string | null): TaskReferenceStatus | null {
    return key ? this.taskStatuses().get(key.toUpperCase()) ?? null : null;
  }

  private load(project: string): void {
    this.loading.set(true);
    this.error.set(null);
    this.http.get<{ items: ProjectProposal[] }>(`/api/projects/${encodeURIComponent(project)}/proposals`)
      .pipe(finalize(() => this.loading.set(false)))
      .subscribe({
        next: response => {
          this.items.set(response.items ?? []);
          const current = this.selectedId();
          if (!current || !response.items.some(p => p.id === current)) this.selectedId.set(response.items[0]?.id ?? null);
          this.hydrateTasks();
        },
        error: () => this.error.set('Could not load project proposals.'),
      });
  }

  private hydrateTasks(): void {
    const keys = this.items().map(p => p.spawnedTask).filter((key): key is string => !!key);
    if (!keys.length) { this.taskStatuses.set(new Map()); return; }
    this.tasks.getReferenceStatuses(keys).subscribe(statuses => {
      this.taskStatuses.set(new Map(statuses.map(status => [status.key.toUpperCase(), status])));
    });
  }
}
