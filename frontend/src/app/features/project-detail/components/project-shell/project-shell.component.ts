import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import {
  PROJECT_RAIL_ITEMS,
  PROJECT_RAIL_PARENT_KEYS,
  ProjectRailGroup,
  ProjectRailItem,
  ProjectRailKey,
} from './project-shell.config';

/** A top-level rail entry plus any tree-expandable children beneath it. */
export interface ProjectRailNode {
  item: ProjectRailItem;
  children: readonly ProjectRailItem[];
}

/** A collapsible main segment of the rail with its top-level nodes. */
export interface ProjectRailGroupView {
  id: ProjectRailGroup;
  label: string;
  nodes: readonly ProjectRailNode[];
}

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

  /** Main segments the user has collapsed (hidden contents). */
  private readonly collapsedGroups = signal<ReadonlySet<ProjectRailGroup>>(new Set());
  /**
   * Tree parents whose children are currently shown. Seeded with every parent
   * so the tree opens fully expanded — keeps existing deep-links to nested
   * rails (Architecture / Wiki / Agent Docs) reachable on first paint.
   */
  private readonly expandedParents = signal<ReadonlySet<ProjectRailKey>>(
    new Set(PROJECT_RAIL_PARENT_KEYS),
  );

  /**
   * Rail items as a collapsible-segment → tree-node structure. Top-level nodes
   * are items without a `parent`; each carries the children that point back at
   * its key. Group order follows the canonical Project Hub taxonomy:
   * Insight → Quality → Operations → Config.
   */
  readonly railGroups = computed<readonly ProjectRailGroupView[]>(() => {
    const order: { id: ProjectRailGroup; label: string }[] = [
      { id: 'insight',    label: 'Insight' },
      { id: 'quality',    label: 'Quality' },
      { id: 'operations', label: 'Operations' },
      { id: 'config',     label: 'Config' },
    ];
    return order.map(({ id, label }) => {
      const inGroup = PROJECT_RAIL_ITEMS.filter(i => i.group === id);
      const nodes = inGroup
        .filter(i => !i.parent)
        .map<ProjectRailNode>(item => ({
          item,
          children: inGroup.filter(c => c.parent === item.key),
        }));
      return { id, label, nodes };
    });
  });

  /** Panel descriptor for the currently selected rail key. */
  readonly activeItem = computed<ProjectRailItem>(() => {
    const key = this.activeRail();
    return PROJECT_RAIL_ITEMS.find(i => i.key === key) ?? PROJECT_RAIL_ITEMS[0];
  });

  isGroupCollapsed(id: ProjectRailGroup): boolean {
    return this.collapsedGroups().has(id);
  }

  toggleGroup(id: ProjectRailGroup): void {
    const next = new Set(this.collapsedGroups());
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.collapsedGroups.set(next);
  }

  isExpanded(key: ProjectRailKey): boolean {
    return this.expandedParents().has(key);
  }

  toggleParent(key: ProjectRailKey): void {
    const next = new Set(this.expandedParents());
    if (next.has(key)) next.delete(key);
    else next.add(key);
    this.expandedParents.set(next);
  }

  /**
   * Row click. A pure container (navigable === false) only toggles its
   * children; a navigable row routes to its panel (no-op when already active).
   * The disclosure twisty is a separate control, so a navigable parent can be
   * selected without forcing its children open or shut.
   */
  onRailClick(item: ProjectRailItem): void {
    if (item.navigable === false) {
      this.toggleParent(item.key);
      return;
    }
    if (item.key === this.activeRail()) return;
    this.railChange.emit(item.key);
  }
}
