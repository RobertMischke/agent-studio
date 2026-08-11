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
import { catchError, of } from 'rxjs';
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import type { TaskReferenceStatus } from '../../../../components/task-reference-microcard/task-reference-microcard';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { TaskService } from '../../../../services/task.service';
import { WorkbenchOverviewRowComponent } from './workbench-overview-row/workbench-overview-row.component';
import type { WorkbenchOverview, WorkbenchOverviewItem } from '../../../../models/project-docs.model';

@Component({
  selector: 'app-workbench-overview',
  standalone: true,
  imports: [LoadingSurfaceComponent, WorkbenchOverviewRowComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-overview.component.html',
  styleUrl: './workbench-overview.component.scss',
})
export class WorkbenchOverviewComponent {
  readonly projectName = input<string | null>(null);
  readonly openWorkbench = output<WorkbenchOverviewItem>();

  private readonly docs = inject(ProjectDocsService);
  private readonly hub = inject(JobsHubClient);
  private readonly tasks = inject(TaskService);
  private readonly destroyRef = inject(DestroyRef);
  private refreshHandle: ReturnType<typeof setTimeout> | null = null;
  private requestGeneration = 0;

  readonly overview = signal<WorkbenchOverview | null>(null);
  readonly loading = signal(false);
  readonly error = signal(false);
  readonly discardedOpen = signal(false);
  readonly completedOpen = signal(false);
  readonly expandedDecisionKey = signal<string | null>(null);
  readonly referenceKeysByItem = signal<ReadonlyMap<string, readonly string[]>>(new Map());
  readonly taskStatusesByItem = signal<ReadonlyMap<string, readonly TaskReferenceStatus[]>>(new Map());
  readonly taskStatusesLoading = signal(false);

  readonly decisionPending = computed(() => this.itemsWithStatus('decision-pending'));
  readonly active = computed(() => this.itemsWithStatus('active'));
  readonly tracking = computed(() => this.itemsWithStatus('decided'));
  readonly invalid = computed(() => this.itemsWithStatus('invalid'));
  readonly discarded = computed(() => this.itemsWithStatus('archived'));
  readonly documented = computed(() => this.itemsWithStatus('documented'));
  readonly currentCount = computed(() =>
    this.decisionPending().length + this.active().length + this.tracking().length,
  );
  readonly historyCount = computed(() => this.discarded().length + this.documented().length);

  constructor() {
    effect(() => {
      const projectName = this.projectName();
      untracked(() => {
        this.discardedOpen.set(false);
        this.completedOpen.set(false);
        this.expandedDecisionKey.set(null);
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

  toggleInlineDecision(item: WorkbenchOverviewItem): void {
    const key = this.itemKey(item);
    this.expandedDecisionKey.update(current => current === key ? null : key);
  }

  inlineDecisionExpanded(item: WorkbenchOverviewItem): boolean {
    return this.expandedDecisionKey() === this.itemKey(item);
  }

  toggleDiscarded(): void {
    this.discardedOpen.update(value => !value);
  }

  toggleCompleted(): void {
    this.completedOpen.update(value => !value);
  }

  dossierCountLabel(count: number): string {
    return `${count} ${count === 1 ? 'Dossier' : 'Dossiers'}`;
  }

  referenceKeys(item: WorkbenchOverviewItem): readonly string[] {
    return this.referenceKeysByItem().get(this.itemKey(item)) ?? [];
  }

  taskStatuses(item: WorkbenchOverviewItem): readonly TaskReferenceStatus[] {
    return this.taskStatusesByItem().get(this.itemKey(item)) ?? [];
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

  itemKey(item: WorkbenchOverviewItem): string {
    return `${item.projectName}:${item.workbench.id}`;
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
        this.hydrateTaskReferences(overview, generation);
      },
      error: () => {
        if (generation !== this.requestGeneration) return;
        this.error.set(true);
        this.loading.set(false);
        this.referenceKeysByItem.set(new Map());
        this.taskStatusesByItem.set(new Map());
        this.taskStatusesLoading.set(false);
      },
    });
  }

  private hydrateTaskReferences(overview: WorkbenchOverview, generation: number): void {
    const currentItems = overview.items.filter(item =>
      ['decision-pending', 'active', 'decided'].includes(item.workbench.status),
    );
    const keysByItem = new Map<string, readonly string[]>();
    const allKeys: string[] = [];
    for (const item of currentItems) {
      const keys = uniqueKeys([
        ...(item.workbench.documentation?.references.map(reference => reference.key) ?? []),
        ...(item.workbench.relatedTaskKeys ?? []),
      ]);
      keysByItem.set(this.itemKey(item), keys);
      allKeys.push(...keys);
    }

    const unique = uniqueKeys(allKeys);
    this.referenceKeysByItem.set(keysByItem);
    this.taskStatusesByItem.set(new Map());
    this.taskStatusesLoading.set(unique.length > 0);
    if (unique.length === 0) return;

    this.tasks.getReferenceStatuses(unique).pipe(
      catchError(() => of([] as TaskReferenceStatus[])),
    ).subscribe(statuses => {
      if (generation !== this.requestGeneration) return;
      const byKey = new Map(statuses.map(status => [normalizeKey(status.key), status]));
      const byItem = new Map<string, readonly TaskReferenceStatus[]>();
      for (const item of currentItems) {
        const itemKeys = keysByItem.get(this.itemKey(item)) ?? [];
        byItem.set(this.itemKey(item), itemKeys.map(key =>
          byKey.get(normalizeKey(key)) ?? ghostStatus(key, item),
        ));
      }
      this.taskStatusesByItem.set(byItem);
      this.taskStatusesLoading.set(false);
    });
  }
}

function uniqueKeys(keys: readonly string[]): string[] {
  const result: string[] = [];
  const seen = new Set<string>();
  for (const key of keys) {
    const normalized = normalizeKey(key);
    if (!normalized || seen.has(normalized)) continue;
    seen.add(normalized);
    result.push(key.trim());
  }
  return result;
}

function normalizeKey(value: string | null | undefined): string {
  return (value ?? '').trim().toUpperCase();
}

function ghostStatus(key: string, item: WorkbenchOverviewItem): TaskReferenceStatus {
  return {
    key,
    exists: false,
    taskKey: null,
    title: null,
    lane: null,
    projectId: '',
    projectName: item.projectName,
    projectColor: item.projectColor ?? null,
    merge: null,
    reviewGrade: null,
  };
}
