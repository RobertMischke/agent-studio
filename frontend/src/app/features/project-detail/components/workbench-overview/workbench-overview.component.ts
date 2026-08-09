import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
} from '@angular/core';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import type {
  WorkbenchOverview,
  WorkbenchOverviewItem,
} from '../../../../models/project-docs.model';

@Component({
  selector: 'app-workbench-overview',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-overview.component.html',
  styleUrl: './workbench-overview.component.scss',
})
export class WorkbenchOverviewComponent {
  readonly projectName = input<string | null>(null);
  readonly openWorkbench = output<WorkbenchOverviewItem>();

  private readonly docs = inject(ProjectDocsService);
  private readonly hub = inject(JobsHubClient);
  private readonly destroyRef = inject(DestroyRef);
  private refreshHandle: ReturnType<typeof setTimeout> | null = null;
  private requestGeneration = 0;

  readonly overview = signal<WorkbenchOverview | null>(null);
  readonly loading = signal(false);
  readonly error = signal(false);
  readonly discardedOpen = signal(false);
  readonly completedOpen = signal(false);

  readonly decisionPending = computed(() => this.itemsWithStatus('decision-pending'));
  readonly active = computed(() => this.itemsWithStatus('active'));
  readonly invalid = computed(() => this.itemsWithStatus('invalid'));
  readonly discarded = computed(() => this.itemsWithStatus('archived'));
  readonly completed = computed(() => this.itemsWithStatus('decided'));

  constructor() {
    effect(() => {
      const projectName = this.projectName();
      untracked(() => {
        this.discardedOpen.set(false);
        this.completedOpen.set(false);
        this.load(projectName, true);
      });
    });

    effect(() => {
      const event = this.hub.workbenchEvent();
      if (!event) return;
      const scope = this.projectName();
      if (event.projectName && scope && event.projectName !== scope) return;
      untracked(() => this.scheduleRefresh());
    });

    this.destroyRef.onDestroy(() => {
      if (this.refreshHandle) clearTimeout(this.refreshHandle);
    });
  }

  open(item: WorkbenchOverviewItem): void {
    if (item.workbench.valid) this.openWorkbench.emit(item);
  }

  toggleDiscarded(): void {
    this.discardedOpen.update(value => !value);
  }

  toggleCompleted(): void {
    this.completedOpen.update(value => !value);
  }

  openDecisionCount(item: WorkbenchOverviewItem): number {
    return item.workbench.openDecisionCount
      ?? (item.workbench.status === 'decision-pending' ? 1 : 0);
  }

  statusLabel(item: WorkbenchOverviewItem): string {
    const workbench = item.workbench;
    if (!workbench.valid) return 'Needs attention';
    if (workbench.status === 'decision-pending') return 'Decision pending';
    if (workbench.status === 'active') return workbench.phase ?? 'Active';
    if (workbench.status === 'archived') return 'Discarded';
    if (workbench.status === 'decided') return 'Completed';
    return workbench.status;
  }

  updatedLabel(value: string): string {
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(value));
  }

  private itemsWithStatus(status: string): WorkbenchOverviewItem[] {
    return (this.overview()?.items ?? []).filter(item => item.workbench.status === status);
  }

  private scheduleRefresh(): void {
    if (this.refreshHandle) return;
    this.refreshHandle = setTimeout(() => {
      this.refreshHandle = null;
      this.load(this.projectName(), false);
    }, 80);
  }

  private load(projectName: string | null, clear: boolean): void {
    const generation = ++this.requestGeneration;
    this.loading.set(true);
    this.error.set(false);
    if (clear) this.overview.set(null);
    this.docs.getWorkbenchOverview(projectName).subscribe({
      next: overview => {
        if (generation !== this.requestGeneration) return;
        this.overview.set(overview);
        this.loading.set(false);
      },
      error: () => {
        if (generation !== this.requestGeneration) return;
        this.error.set(true);
        this.loading.set(false);
      },
    });
  }
}
