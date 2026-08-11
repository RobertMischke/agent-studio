import { NgTemplateOutlet } from '@angular/common';
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
import { StudioIconComponent, type StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { WorkbenchOverviewControlsComponent } from '../workbench-overview-controls/workbench-overview-controls.component';
import { WorkbenchViewerComponent } from '../workbench-viewer/workbench-viewer.component';
import type {
  ArticlePattern,
  WorkbenchOverview,
  WorkbenchOverviewItem,
} from '../../../../models/project-docs.model';
import {
  projectWorkbenchOverviewItems,
  WorkbenchOverviewViewState,
  type WorkbenchOverviewSortKey,
  workbenchOverviewKey,
  workbenchOverviewStatusLabel,
} from './workbench-overview-state';

@Component({
  selector: 'app-workbench-overview',
  standalone: true,
  imports: [
    LoadingSurfaceComponent,
    NgTemplateOutlet,
    StudioIconComponent,
    WorkbenchOverviewControlsComponent,
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
  private readonly destroyRef = inject(DestroyRef);
  private refreshHandle: ReturnType<typeof setTimeout> | null = null;
  private requestGeneration = 0;

  readonly overview = signal<WorkbenchOverview | null>(null);
  readonly loading = signal(false);
  readonly error = signal(false);
  readonly discardedOpen = signal(false);
  readonly completedOpen = signal(false);
  readonly expandedDecisionKey = signal<string | null>(null);
  readonly viewState = new WorkbenchOverviewViewState();

  readonly visibleItems = computed(() => projectWorkbenchOverviewItems(
    this.overview()?.items ?? [],
    {
      query: this.viewState.query(),
      sortKey: this.viewState.sortKey(),
      direction: this.viewState.direction(),
    },
  ));
  readonly visibleCount = computed(() => this.visibleItems().length);
  readonly hasFilter = computed(() => this.viewState.query().trim().length > 0);
  readonly usesDefaultSort = computed(() => this.viewState.sortKey() === 'default');

  readonly decisionPending = computed(() => this.itemsWithStatus('decision-pending'));
  readonly active = computed(() => this.itemsWithStatus('active'));
  readonly tracking = computed(() => this.itemsWithStatus('decided'));
  readonly invalid = computed(() => this.itemsWithStatus('invalid'));
  readonly current = computed(() => this.visibleItems().filter(item =>
    ['active', 'decided', 'invalid'].includes(item.workbench.status),
  ));
  readonly currentQueue = computed(() => this.visibleItems().filter(item =>
    ['decision-pending', 'active', 'decided', 'invalid'].includes(item.workbench.status),
  ));
  readonly discarded = computed(() => this.itemsWithStatus('archived'));
  readonly documented = computed(() => this.itemsWithStatus('documented'));

  constructor() {
    effect(() => {
      const projectName = this.projectName();
      untracked(() => {
        this.viewState.hydrate();
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

    if (typeof window !== 'undefined') {
      const restoreViewState = () => this.viewState.hydrate();
      window.addEventListener('hashchange', restoreViewState);
      window.addEventListener('popstate', restoreViewState);
      this.destroyRef.onDestroy(() => {
        window.removeEventListener('hashchange', restoreViewState);
        window.removeEventListener('popstate', restoreViewState);
      });
    }
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

  selectSort(sortKey: WorkbenchOverviewSortKey): void {
    this.viewState.selectSort(sortKey);
  }

  keyLabel(item: WorkbenchOverviewItem): string {
    return workbenchOverviewKey(item);
  }

  documentPattern(item: WorkbenchOverviewItem): ArticlePattern {
    return item.workbench.pattern === 'ui' ? 'ui' : 'concept';
  }

  patternIcon(item: WorkbenchOverviewItem): StudioIconName {
    return this.documentPattern(item) === 'ui' ? 'grid' : 'book';
  }

  statusLabel(item: WorkbenchOverviewItem): string {
    return workbenchOverviewStatusLabel(item.workbench);
  }

  updatedLabel(value: string): string {
    return new Intl.DateTimeFormat(undefined, {
      dateStyle: 'medium',
      timeStyle: 'short',
    }).format(new Date(value));
  }

  private itemsWithStatus(status: string): WorkbenchOverviewItem[] {
    return this.visibleItems().filter(item => item.workbench.status === status);
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
