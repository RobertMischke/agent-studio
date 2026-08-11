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
import { LoadingSurfaceComponent } from '../../../../components/async-feedback';
import {
  TaskReferenceMicrocardComponent,
  type TaskReferenceStatus,
} from '../../../../components/task-reference-microcard/task-reference-microcard';
import { StudioIconComponent, type StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { ProjectLookupService } from '../../../../services/project-lookup.service';
import { TaskService } from '../../../../services/task.service';
import { WorkbenchViewerComponent } from '../workbench-viewer/workbench-viewer.component';
import type {
  ArticlePattern,
  WorkbenchOverview,
  WorkbenchOverviewItem,
} from '../../../../models/project-docs.model';

@Component({
  selector: 'app-workbench-overview',
  standalone: true,
  imports: [
    LoadingSurfaceComponent,
    StudioIconComponent,
    TaskReferenceMicrocardComponent,
    WorkbenchViewerComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workbench-overview.component.html',
  styleUrl: './workbench-overview.component.scss',
})
export class WorkbenchOverviewComponent {
  readonly projectName = input<string | null>(null);
  readonly openWorkbench = output<WorkbenchOverviewItem>();

  private readonly docs = inject(ProjectDocsService);
  private readonly hub = inject(JobsHubClient);
  private readonly projects = inject(ProjectLookupService);
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
  readonly referenceStatusesByItem = signal<ReadonlyMap<string, readonly TaskReferenceStatus[]>>(new Map());
  readonly referenceStatusesLoading = signal(false);

  readonly decisionPending = computed(() => this.itemsWithStatus('decision-pending'));
  readonly active = computed(() => this.itemsWithStatus('active'));
  readonly tracking = computed(() => this.itemsWithStatus('decided'));
  readonly invalid = computed(() => this.itemsWithStatus('invalid'));
  readonly discarded = computed(() => this.itemsWithStatus('archived'));
  readonly documented = computed(() => this.itemsWithStatus('documented'));
  readonly current = computed(() => (this.overview()?.items ?? []).filter(
    item => item.workbench.status === 'active' || item.workbench.status === 'decided',
  ));
  readonly currentCount = computed(() => this.decisionPending().length + this.current().length);
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

  openDecisionCount(item: WorkbenchOverviewItem): number {
    return item.workbench.openDecisionCount
      ?? (item.workbench.status === 'decision-pending' ? 1 : 0);
  }

  documentPattern(item: WorkbenchOverviewItem): ArticlePattern {
    return item.workbench.pattern === 'ui' ? 'ui' : 'concept';
  }

  patternIcon(item: WorkbenchOverviewItem): StudioIconName {
    return this.documentPattern(item) === 'ui' ? 'grid' : 'book';
  }

  projectDisplay(item: WorkbenchOverviewItem) {
    return this.projects.getProjectDisplay(item.projectName);
  }

  referenceStatuses(item: WorkbenchOverviewItem): readonly TaskReferenceStatus[] {
    return this.referenceStatusesByItem().get(this.itemKey(item)) ?? [];
  }

  statusLabel(item: WorkbenchOverviewItem): string {
    const workbench = item.workbench;
    if (!workbench.valid) return 'Needs attention';
    if (workbench.status === 'decision-pending') return 'Decision pending';
    if (workbench.status === 'active') return workbench.phase ?? 'Active';
    if (workbench.status === 'decided') return 'Accepted / In progress';
    if (workbench.status === 'archived') return 'Discarded';
    if (workbench.status === 'documented') return 'Documented';
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

  private itemKey(item: WorkbenchOverviewItem): string {
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
    if (clear) {
      this.overview.set(null);
      this.referenceStatusesByItem.set(new Map());
      this.referenceStatusesLoading.set(false);
    }
    this.docs.getWorkbenchOverview(projectName).subscribe({
      next: overview => {
        if (generation !== this.requestGeneration) return;
        this.overview.set(overview);
        this.loading.set(false);
        this.hydrateReferenceStatuses(overview, generation);
      },
      error: () => {
        if (generation !== this.requestGeneration) return;
        this.error.set(true);
        this.loading.set(false);
        this.referenceStatusesByItem.set(new Map());
        this.referenceStatusesLoading.set(false);
      },
    });
  }

  private hydrateReferenceStatuses(overview: WorkbenchOverview, generation: number): void {
    const keysByItem = new Map<string, string[]>();
    const allKeys: string[] = [];
    const seen = new Set<string>();
    for (const item of overview.items) {
      if (!['decision-pending', 'active', 'decided'].includes(item.workbench.status)) continue;
      const keys = uniqueKeys([
        ...(item.workbench.documentation?.references.map(reference => reference.key) ?? []),
        ...(item.workbench.relatedTaskKeys ?? []),
      ]);
      keysByItem.set(this.itemKey(item), keys);
      for (const key of keys) {
        const normalized = normalizeKey(key);
        if (seen.has(normalized)) continue;
        seen.add(normalized);
        allKeys.push(key);
      }
    }

    this.referenceStatusesByItem.set(statusesByItem(keysByItem, new Map(), overview.items));
    if (allKeys.length === 0) {
      this.referenceStatusesLoading.set(false);
      return;
    }

    this.referenceStatusesLoading.set(true);
    this.tasks.getReferenceStatuses(allKeys).subscribe({
      next: statuses => {
        if (generation !== this.requestGeneration) return;
        const byKey = new Map(
          statuses.map(status => [normalizeKey(status.key), status] as const),
        );
        this.referenceStatusesByItem.set(statusesByItem(keysByItem, byKey, overview.items));
        this.referenceStatusesLoading.set(false);
      },
      error: () => {
        if (generation !== this.requestGeneration) return;
        this.referenceStatusesLoading.set(false);
      },
    });
  }
}

function statusesByItem(
  keysByItem: ReadonlyMap<string, readonly string[]>,
  resolved: ReadonlyMap<string, TaskReferenceStatus>,
  items: readonly WorkbenchOverviewItem[],
): ReadonlyMap<string, readonly TaskReferenceStatus[]> {
  const projectByItem = new Map(items.map(item => [`${item.projectName}:${item.workbench.id}`, item.projectName]));
  return new Map([...keysByItem].map(([itemKey, keys]) => [
    itemKey,
    keys.map(key => resolved.get(normalizeKey(key)) ?? ghostStatus(key, projectByItem.get(itemKey) ?? '')),
  ]));
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

function ghostStatus(key: string, projectName: string): TaskReferenceStatus {
  return {
    key,
    exists: false,
    taskKey: null,
    title: null,
    lane: null,
    projectId: '',
    projectName,
    projectColor: null,
    merge: null,
    reviewGrade: null,
  };
}
