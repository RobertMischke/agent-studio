import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChildren,
} from '@angular/core';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import type { WorkbenchCatalogue, WorkbenchListItem } from '../../../../models/project-docs.model';

const EXPANDED_WORKBENCH_SECTIONS_KEY = 'atp.studio.explorer.workbenches.expanded.v1';

@Component({
  selector: 'app-explorer-workbench-list',
  standalone: true,
  imports: [TreeRowComponent, AppTooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './explorer-workbench-list.component.html',
  styleUrl: './explorer-workbench-list.component.scss',
})
export class ExplorerWorkbenchListComponent {
  readonly projectName = input.required<string>();
  readonly activeWorkbenchId = input<string | null>(null);
  readonly openWorkbench = output<WorkbenchListItem>();
  /** Jump to the project wiki (the workbench pages live there). */
  readonly openWiki = output<void>();
  private readonly docs = inject(ProjectDocsService);
  readonly expanded = signal(false);
  readonly loading = signal(false);
  readonly catalogue = signal<WorkbenchCatalogue | null>(null);
  readonly historyCatalogue = signal<WorkbenchCatalogue | null>(null);
  readonly historyOpen = signal(false);
  readonly documentedHistory = computed(() => (this.historyCatalogue()?.items ?? [])
    .filter(item => item.status === 'documented'));
  readonly archivedHistory = computed(() => (this.historyCatalogue()?.items ?? [])
    .filter(item => item.status === 'archived'));
  private readonly topics = viewChildren<ElementRef<HTMLButtonElement>>('workbenchTopic');
  private lastProjectName: string | null = null;
  private lastRevealedWorkbench: string | null = null;

  constructor() {
    effect(() => {
      const projectName = this.projectName();
      const activeWorkbenchId = this.activeWorkbenchId();
      const projectChanged = projectName !== this.lastProjectName;

      if (projectChanged) {
        this.lastProjectName = projectName;
        this.lastRevealedWorkbench = null;
        untracked(() => {
          this.catalogue.set(null);
          this.historyCatalogue.set(null);
          this.historyOpen.set(false);
          this.expanded.set(readExpandedProjects().has(projectName));
          if (this.expanded()) this.loadCatalogue();
        });
      }

      if (!activeWorkbenchId) {
        this.lastRevealedWorkbench = null;
        return;
      }

      const revealKey = `${projectName}:${activeWorkbenchId}`;
      if (this.lastRevealedWorkbench === revealKey) return;
      this.lastRevealedWorkbench = revealKey;
      untracked(() => {
        this.setExpanded(true);
        this.loadCatalogue();
        this.revealHistoryItem(activeWorkbenchId);
      });
    });

    effect(() => {
      const activeWorkbenchId = this.activeWorkbenchId();
      const activeTopic = this.topics()
        .map(topic => topic.nativeElement)
        .find(topic => topic.getAttribute('aria-current') === 'page');
      if (!activeWorkbenchId || !activeTopic || typeof activeTopic.scrollIntoView !== 'function') return;
      queueMicrotask(() => activeTopic.scrollIntoView({ block: 'nearest', inline: 'nearest' }));
    });
  }

  toggle(): void {
    this.setExpanded(!this.expanded());
    if (this.expanded()) this.loadCatalogue();
  }

  private setExpanded(expanded: boolean): void {
    this.expanded.set(expanded);
    const projects = readExpandedProjects();
    if (expanded) projects.add(this.projectName());
    else projects.delete(this.projectName());
    writeExpandedProjects(projects);
  }

  private loadCatalogue(): void {
    if (this.catalogue() || this.loading()) return;
    this.loading.set(true);
    this.docs.getWorkbenches(this.projectName()).subscribe({
      next: value => {
        this.catalogue.set(value);
        this.loading.set(false);
        const activeWorkbenchId = this.activeWorkbenchId();
        if (activeWorkbenchId) this.revealHistoryItem(activeWorkbenchId);
      },
      error: () => this.loading.set(false),
    });
  }

  toggleHistory(): void {
    this.historyOpen.update(value => !value);
    if (this.historyOpen()) this.loadHistoryCatalogue();
  }

  private loadHistoryCatalogue(): void {
    if (this.historyCatalogue() || this.loading()) return;
    this.loading.set(true);
    this.docs.getWorkbenches(this.projectName(), true).subscribe({
      next: value => { this.historyCatalogue.set(value); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  isActive(item: WorkbenchListItem): boolean {
    return item.id === this.activeWorkbenchId();
  }

  isAcute(item: WorkbenchListItem): boolean {
    return !item.valid || item.status === 'decision-pending';
  }

  secondaryMeta(item: WorkbenchListItem): string {
    if (!item.valid) return 'Needs attention';
    if (item.documentation?.eligible) return 'Ready to document';
    if (item.status === 'decision-pending') return 'Decision pending';
    if (item.status === 'active') return item.phase ?? 'Active';
    if (item.status === 'decided') return 'Tracking';
    if (item.status === 'documented') return 'Documented';
    if (item.status === 'archived') return 'Archived';
    return item.status;
  }

  accessibleMeta(item: WorkbenchListItem): string {
    const days = Math.max(0, Math.floor((Date.now() - new Date(item.updatedAtUtc).getTime()) / 86_400_000));
    const updated = days === 0 ? 'updated today' : `updated ${days} days ago`;
    return `${this.secondaryMeta(item)}, ${updated}`;
  }

  navTooltip(item: WorkbenchListItem): string {
    return [item.key, item.title, this.accessibleMeta(item)].filter(Boolean).join(' · ');
  }

  private revealHistoryItem(activeWorkbenchId: string): void {
    const catalogue = this.catalogue();
    if (!catalogue || catalogue.items.some(item => item.id === activeWorkbenchId)) return;
    this.historyOpen.set(true);
    this.loadHistoryCatalogue();
  }
}

function readExpandedProjects(): Set<string> {
  if (typeof window === 'undefined') return new Set<string>();
  try {
    const value = JSON.parse(window.localStorage?.getItem(EXPANDED_WORKBENCH_SECTIONS_KEY) ?? '[]') as unknown;
    if (!Array.isArray(value)) return new Set<string>();
    return new Set(value.filter((projectName): projectName is string => typeof projectName === 'string'));
  } catch {
    return new Set<string>();
  }
}

function writeExpandedProjects(projects: ReadonlySet<string>): void {
  if (typeof window === 'undefined') return;
  try {
    window.localStorage?.setItem(EXPANDED_WORKBENCH_SECTIONS_KEY, JSON.stringify([...projects]));
  } catch {
    /* storage may be full or blocked */
  }
}
