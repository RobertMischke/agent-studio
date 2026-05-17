import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import {
  PROJECT_RAIL_ITEMS,
  ProjectRailGroup,
  ProjectRailItem,
  ProjectRailKey,
} from './project-shell.config';

/**
 * Project page shell. Slice 2 of the quality-system mockup: introduces the
 * left-rail navigation skeleton plus a placeholder body per rail item.
 *
 * The shell is action-driven by design (see docs/mockups/quality-system/
 * README.md): mounting a panel must not run any analysis. Real content for
 * each rail item lands in a separate follow-up slice.
 */
@Component({
  selector: 'app-project-shell',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-shell.component.html',
  styleUrl: './project-shell.component.scss',
})
export class ProjectShellComponent {
  readonly projectName = input.required<string>();
  readonly activeRail = input.required<ProjectRailKey>();
  /**
   * When true, the shell hides its built-in panel header + empty state and
   * surfaces an `<ng-content>` slot in the panel body instead. The host
   * (typically `app.ts`) projects a real panel component (Security panel,
   * etc.) and owns the panel header so the slice can paint its own
   * baseline badge / cards / actions per the mockup. When false, the
   * generic placeholder + description still renders for rails that have
   * not landed their content slice yet.
   */
  readonly hasCustomPanel = input<boolean>(false);
  readonly railChange = output<ProjectRailKey>();
  readonly openFeed = output<void>();
  readonly closeShell = output<void>();

  /** Rail items grouped for the side nav (PROJECT / CONFIGURATION). */
  readonly railGroups = computed<readonly { id: ProjectRailGroup; label: string; items: readonly ProjectRailItem[] }[]>(() => {
    // Group order follows the reference's Project Hub side-nav:
    // Insight → Quality → Operations → Config. The names + ordering
    // are the canonical taxonomy the user expects in the shell.
    const groups: { id: ProjectRailGroup; label: string; items: ProjectRailItem[] }[] = [
      { id: 'insight',    label: 'Insight',    items: [] },
      { id: 'quality',    label: 'Quality',    items: [] },
      { id: 'operations', label: 'Operations', items: [] },
      { id: 'config',     label: 'Config',     items: [] },
    ];
    for (const item of PROJECT_RAIL_ITEMS) {
      const bucket = groups.find(g => g.id === item.group);
      bucket?.items.push(item);
    }
    return groups;
  });

  /** Panel descriptor for the currently selected rail key. */
  readonly activeItem = computed<ProjectRailItem>(() => {
    const key = this.activeRail();
    return PROJECT_RAIL_ITEMS.find(i => i.key === key) ?? PROJECT_RAIL_ITEMS[0];
  });

  onRailClick(key: ProjectRailKey): void {
    if (key === this.activeRail()) return;
    this.railChange.emit(key);
  }
}
