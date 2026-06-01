import {
  ChangeDetectionStrategy,
  Component,
  ViewEncapsulation,
  computed,
  inject,
  input,
  output,
} from '@angular/core';
import type { RegistryWorkspaceListItem } from '../../../../models/task.model';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { SectionHeaderComponent } from '../../../../components/section-header/section-header.component';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import { ProjectDragDropService } from '../../../shell';
import { ExplorerSectionsService } from '../../services/explorer-sections.service';

/** Per-lane working-set breakdown for a project's expandable child rows. */
export interface ExplorerLaneCounts {
  backlog: number;
  active: number;
  review: number;
  archive: number;
}

/** Flat project row as computed by the shell (`ProjectSidebarRow`). */
export interface ExplorerProjectRow {
  name: string;
  initial: string;
  color: string;
  totalJobs: number;
  isActive: boolean;
}

/** A project row decorated with its lane counts, ready to render. */
export interface ExplorerProjectNode extends ExplorerProjectRow {
  lanes: ExplorerLaneCounts;
}

/** One workspace folder and the project rows that belong to it. */
export interface ExplorerWorkspaceGroup {
  id: string;
  displayName: string;
  color: string | null;
  projects: ExplorerProjectNode[];
}

const ZERO_LANES: ExplorerLaneCounts = { backlog: 0, active: 0, review: 0, archive: 0 };

function folderTail(path: string): string {
  const parts = path.split(/[\\/]+/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : path;
}

/**
 * F46 — Explorer two-level workspace → project tree.
 *
 * The Explorer's original single "Workspace" header dates from F27, before
 * the F45 registry introduced real, multiple workspaces. This component
 * groups the shell's flat project rows under their owning registry
 * workspace so the sidebar reflects the workspace → project hierarchy.
 *
 * It is purely presentational over the data the shell already loads:
 * project rows (job-derived) joined to {@link RegistryWorkspaceListItem}s by
 * display name or storage-folder tail. Rows that match no registry project
 * fall into an "Unassigned" group; when the registry is empty (in-memory
 * mode / not yet loaded) it falls back to a single legacy folder so the
 * Explorer never goes blank.
 *
 * Project drag-and-drop, expand/collapse, row testids and BEM classes are
 * preserved verbatim from the shell — management (rename / colour / move /
 * delete) deliberately stays in the Settings panel (F47 / F66 / ADR-0048);
 * this is navigation only.
 */
@Component({
  selector: 'app-explorer-workspace-tree',
  standalone: true,
  imports: [SectionHeaderComponent, TreeRowComponent, StudioIconComponent, EmptyStateComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './explorer-workspace-tree.component.html',
  styleUrl: './explorer-workspace-tree.component.scss',
})
export class ExplorerWorkspaceTreeComponent {
  readonly projectRows = input<readonly ExplorerProjectRow[]>([]);
  readonly registryWorkspaces = input<readonly RegistryWorkspaceListItem[]>([]);
  readonly projectLanes = input<ReadonlyMap<string, ExplorerLaneCounts>>(new Map());
  readonly expandedProjects = input<ReadonlySet<string>>(new Set());
  readonly showAllActive = input(false);

  readonly showAll = output<void>();
  readonly toggleExpanded = output<string>();
  readonly openBoardRequest = output<string>();
  readonly openHubRequest = output<string>();
  readonly projectDrop = output<{ event: DragEvent; name: string }>();

  readonly projectDrag = inject(ProjectDragDropService);
  private readonly sections = inject(ExplorerSectionsService);

  readonly totalProjectCount = computed(() => this.projectRows().length);

  readonly groups = computed<ExplorerWorkspaceGroup[]>(() => {
    const rows = this.projectRows();
    const lanes = this.projectLanes();
    const node = (r: ExplorerProjectRow): ExplorerProjectNode => ({
      ...r,
      lanes: lanes.get(r.name) ?? ZERO_LANES,
    });

    const workspaces = [...this.registryWorkspaces()].sort((a, b) => a.sortOrder - b.sortOrder);
    if (workspaces.length === 0) {
      return rows.length
        ? [{ id: '__all__', displayName: 'Workspace', color: null, projects: rows.map(node) }]
        : [];
    }

    const byName = new Map(rows.map(r => [r.name, r] as const));
    const used = new Set<string>();
    const groups: ExplorerWorkspaceGroup[] = [];
    for (const ws of workspaces) {
      const projects: ExplorerProjectNode[] = [];
      for (const rp of ws.projects) {
        const match = byName.get(rp.displayName) ?? byName.get(folderTail(rp.storageLocation));
        if (match && !used.has(match.name)) {
          used.add(match.name);
          projects.push(node(match));
        }
      }
      projects.sort((a, b) => a.name.localeCompare(b.name));
      groups.push({ id: ws.id, displayName: ws.displayName, color: ws.color, projects });
    }

    const leftover = rows
      .filter(r => !used.has(r.name))
      .sort((a, b) => a.name.localeCompare(b.name))
      .map(node);
    if (leftover.length) {
      groups.push({ id: '__unassigned__', displayName: 'Unassigned', color: null, projects: leftover });
    }
    return groups;
  });

  isCollapsed(key: string): boolean {
    return this.sections.isCollapsed(key);
  }

  setCollapsed(key: string, collapsed: boolean): void {
    this.sections.setCollapsed(key, collapsed);
  }

  isExpanded(name: string): boolean {
    return this.expandedProjects().has(name);
  }

  canDrop(source: string | null, target: string): boolean {
    return this.projectDrag.canDropProjectOn(source, target);
  }

  /** Hover-title for a project row, mirroring the shell's drag affordances. */
  rowTitle(name: string): string {
    const source = this.projectDrag.draggingProjectName();
    if (!source || source === name) {
      return 'Drag to move this project to another workspace';
    }
    if (this.projectDrag.canDropProjectOn(source, name)) {
      return `Drop here to move ${source} to workspace ${name}`;
    }
    return 'Not a valid drop target';
  }

  onDragStart(event: DragEvent, name: string): void {
    this.projectDrag.onDragStart(event, name);
  }

  onDragOver(event: DragEvent, name: string): void {
    this.projectDrag.onDragOver(event, name);
  }

  onDragLeave(name: string): void {
    this.projectDrag.onDragLeave(name);
  }

  onDragEnd(): void {
    this.projectDrag.onDragEnd();
  }

  onDrop(event: DragEvent, name: string): void {
    this.projectDrop.emit({ event, name });
  }
}
