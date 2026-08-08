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
import { ProjectDocsService } from '../../../../services/project-docs.service';
import type { WorkbenchCatalogue, WorkbenchListItem } from '../../../../models/project-docs.model';
import { ExplorerWorkbenchStateService } from '../../services/explorer-workbench-state.service';

@Component({
  selector: 'app-explorer-workbench-list',
  standalone: true,
  imports: [TreeRowComponent],
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
  private readonly expansionState = inject(ExplorerWorkbenchStateService);
  private readonly topicElements = viewChildren<ElementRef<HTMLButtonElement>>('workbenchTopic');
  readonly expanded = computed(() => this.expansionState.isExpanded(this.projectName()));
  readonly active = computed(() => this.activeWorkbenchId() !== null);
  readonly loading = signal(false);
  readonly catalogue = signal<WorkbenchCatalogue | null>(null);
  readonly historyCatalogue = signal<WorkbenchCatalogue | null>(null);
  readonly historyOpen = signal(false);
  readonly settledHistory = computed(() => (this.historyCatalogue()?.items ?? [])
    .filter(item => item.status === 'decided' || item.status === 'archived'));

  /** Opening or restoring a Workbench reveals its project-scoped branch. */
  private readonly revealActiveWorkbenchFx = effect(() => {
    const projectName = this.projectName();
    const activeWorkbenchId = this.activeWorkbenchId();
    if (!activeWorkbenchId) return;
    untracked(() => this.expansionState.setExpanded(projectName, true));
  });

  /** Expanded state, including a persisted state restored on boot, loads lazily. */
  private readonly loadExpandedCatalogueFx = effect(() => {
    const projectName = this.projectName();
    if (!this.expanded()) return;
    untracked(() => this.loadCatalogue(projectName));
  });

  /** Keep the exact active entry visible after its lazy catalogue renders. */
  private readonly revealActiveTopicFx = effect(() => {
    const activeWorkbenchId = this.activeWorkbenchId();
    if (!activeWorkbenchId || !this.expanded()) return;
    const activeElement = this.topicElements()
      .find(ref => ref.nativeElement.dataset['workbenchId'] === activeWorkbenchId)
      ?.nativeElement;
    if (!activeElement || typeof activeElement.scrollIntoView !== 'function') return;

    queueMicrotask(() => {
      if (!activeElement.isConnected) return;
      const reduceMotion = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches === true;
      activeElement.scrollIntoView({
        behavior: reduceMotion ? 'auto' : 'smooth',
        block: 'nearest',
        inline: 'nearest',
      });
    });
  });

  toggle(): void {
    const projectName = this.projectName();
    const expanded = !this.expanded();
    this.expansionState.setExpanded(projectName, expanded);
    if (expanded) this.loadCatalogue(projectName);
  }

  private loadCatalogue(projectName: string): void {
    if (this.catalogue() || this.loading()) return;
    this.loading.set(true);
    this.docs.getWorkbenches(projectName).subscribe({
      next: value => { this.catalogue.set(value); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  toggleHistory(): void {
    this.historyOpen.update(value => !value);
    if (!this.historyOpen() || this.historyCatalogue() || this.loading()) return;
    this.loading.set(true);
    this.docs.getWorkbenches(this.projectName(), true).subscribe({
      next: value => { this.historyCatalogue.set(value); this.loading.set(false); },
      error: () => this.loading.set(false),
    });
  }

  isActive(item: WorkbenchListItem): boolean {
    return this.activeWorkbenchId() === item.id;
  }

  attentionLabel(item: WorkbenchListItem): string | null {
    if (!item.valid || item.status === 'invalid') return 'Invalid';
    if (item.status === 'decision-pending') return 'Decision pending';
    return null;
  }

  itemAriaLabel(item: WorkbenchListItem): string {
    const detail = [
      this.isActive(item) ? 'current Workbench' : null,
      this.attentionLabel(item)?.toLocaleLowerCase() ?? null,
    ].filter((value): value is string => value !== null);
    return detail.length > 0 ? `${item.title}, ${detail.join(', ')}` : item.title;
  }
}
