import { ChangeDetectionStrategy, Component, inject, input, output, signal } from '@angular/core';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import type { WorkbenchCatalogue, WorkbenchListItem } from '../../../../models/project-docs.model';

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
  readonly active = input(false);
  readonly openWorkbench = output<WorkbenchListItem>();
  private readonly docs = inject(ProjectDocsService);
  readonly expanded = signal(false);
  readonly loading = signal(false);
  readonly catalogue = signal<WorkbenchCatalogue | null>(null);
  readonly historyCatalogue = signal<WorkbenchCatalogue | null>(null);
  readonly historyOpen = signal(false);

  toggle(): void {
    this.expanded.update(value => !value);
    if (!this.expanded() || this.catalogue() || this.loading()) return;
    this.loading.set(true);
    this.docs.getWorkbenches(this.projectName()).subscribe({
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

  isSettled(item: WorkbenchListItem): boolean {
    return item.status === 'decided' || item.status === 'archived';
  }

  meta(item: WorkbenchListItem): string {
    if (!item.valid) return 'invalid';
    const days = Math.max(0, Math.floor((Date.now() - new Date(item.updatedAtUtc).getTime()) / 86_400_000));
    return `${item.phase ?? item.status} · ${days === 0 ? 'today' : `${days}d`}`;
  }
}
