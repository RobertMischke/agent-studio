import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  ViewEncapsulation,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  viewChild,
} from '@angular/core';
import type { RegistryWorkspaceListItem } from '../../../../models/task.model';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { SectionHeaderComponent } from '../../../../components/section-header/section-header.component';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import { TooltipDirective } from '../../../../components/tooltip';
import { MenuComponent, type MenuItem, type MenuItemClickEvent } from '../../../../components/menu';
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
  /** Registry id (PROJ-NNN) when this row matches a registry project; null
   *  for the empty-registry "__all__" fallback and "__unassigned__" rows,
   *  which have no workspace membership and are therefore not draggable. */
  projectId: string | null;
  /** Owning registry workspace id for matched rows (the drag source's current
   *  workspace, used to reject same-workspace drops); null when unmatched. */
  workspaceId: string | null;
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
 * preserved verbatim from the shell. Colour / move / delete still live in the
 * Settings panel (F47 / F66 / ADR-0048); the one in-tree management affordance
 * is workspace rename, surfaced at the node itself via double-click and a
 * right-click "Rename" context menu so the operator does not have to know the
 * Settings panel exists. Both routes drive the same registry-only inline edit.
 */
@Component({
  selector: 'app-explorer-workspace-tree',
  standalone: true,
  imports: [SectionHeaderComponent, TreeRowComponent, StudioIconComponent, EmptyStateComponent, TooltipDirective, MenuComponent],
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
  /**
   * Emitted when a project row is dropped onto a (different, real) workspace
   * folder. The shell persists it via PUT /api/projects/{projectId}
   * `{ workspaceId }` and reloads the registry so the row re-homes under the
   * new workspace. No job folder is touched on disk (ADR-0048).
   */
  readonly projectDrop = output<{ projectId: string; targetWorkspaceId: string }>();

  /**
   * F46 — workspace-header inline rename. Double-clicking a (real) workspace
   * header swaps it for a text input; committing emits this so the shell can
   * PUT /api/workspaces/{id} and reload. The rename touches registry metadata
   * only — no project folder is moved or renamed on disk.
   *
   * Project-row rename is intentionally NOT wired here: the rows are labelled
   * and keyed by their job-derived name (not the registry displayName), so a
   * registry rename would not change the visible label and would break the
   * workspace→project grouping match. Renaming projects from the tree lands
   * with the projectId-keyed display migration (F46 step 7).
   */
  readonly renameWorkspace = output<{ id: string; displayName: string }>();

  readonly projectDrag = inject(ProjectDragDropService);
  private readonly sections = inject(ExplorerSectionsService);
  private readonly modalStack = inject(ModalStackService);

  /** Id of the workspace header currently in inline-rename mode (null = none). */
  readonly renamingWsId = signal<string | null>(null);
  readonly renameDraft = signal('');

  private readonly renameInputRef = viewChild<ElementRef<HTMLInputElement>>('wsRenameInput');
  private readonly focusRenameFx = effect(() => {
    if (this.renamingWsId() === null) return;
    const el = this.renameInputRef()?.nativeElement;
    if (el) queueMicrotask(() => { el.focus(); el.select(); });
  });

  /**
   * Register the open rename input on the modal stack so Escape cancels it.
   *
   * Escape is arbitrated centrally by {@link ModalStackService}: a single
   * capture-phase document listener invokes the topmost entry's close handler
   * and then `stopImmediatePropagation()`. Always-mounted overlays (menu,
   * info-button, concept-help, …) keep that stack non-empty even in the plain
   * Explorer, so an inline input's own `(keydown)` Escape never fires — the
   * stack top swallows the keystroke first. Pushing ourselves as the LIFO top
   * while editing routes Escape to {@link cancelRename}; Enter is not arbitrated
   * by the stack and still commits via the input's `(keydown)` binding.
   */
  private renameModalDispose: (() => void) | null = null;
  private readonly renameModalFx = effect(() => {
    const renaming = this.renamingWsId() !== null;
    if (renaming && !this.renameModalDispose) {
      this.renameModalDispose = this.modalStack.push('explorer-ws-rename', () => {
        this.cancelRename();
        return true;
      });
    } else if (!renaming && this.renameModalDispose) {
      this.renameModalDispose();
      this.renameModalDispose = null;
    }
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => this.renameModalDispose?.());
  }

  readonly totalProjectCount = computed(() => this.projectRows().length);

  readonly groups = computed<ExplorerWorkspaceGroup[]>(() => {
    const rows = this.projectRows();
    const lanes = this.projectLanes();
    const node = (
      r: ExplorerProjectRow,
      projectId: string | null,
      workspaceId: string | null,
    ): ExplorerProjectNode => ({
      ...r,
      lanes: lanes.get(r.name) ?? ZERO_LANES,
      projectId,
      workspaceId,
    });

    const workspaces = [...this.registryWorkspaces()].sort((a, b) => a.sortOrder - b.sortOrder);
    if (workspaces.length === 0) {
      return rows.length
        ? [{ id: '__all__', displayName: 'Workspace', color: null, projects: rows.map(r => node(r, null, null)) }]
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
          // Carry the registry id + owning workspace so the drag source knows
          // what to reassign and which workspace drop is a no-op.
          projects.push(node(match, rp.id, ws.id));
        }
      }
      projects.sort((a, b) => a.name.localeCompare(b.name));
      groups.push({ id: ws.id, displayName: ws.displayName, color: ws.color, projects });
    }

    const leftover = rows
      .filter(r => !used.has(r.name))
      .sort((a, b) => a.name.localeCompare(b.name))
      .map(r => node(r, null, null));
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

  /**
   * Toggle a workspace folder's collapsed state from the live service value
   * rather than the section-header's `[collapsed]` input. A double-click (used
   * to open inline rename) fires two click events faster than the input
   * re-renders, so both clicks would otherwise read the same stale value and
   * collapse the folder instead of netting back to its prior state.
   */
  onWsHeaderToggle(g: ExplorerWorkspaceGroup): void {
    const key = 'ws:' + g.id;
    this.setCollapsed(key, !this.isCollapsed(key));
  }

  isExpanded(name: string): boolean {
    return this.expandedProjects().has(name);
  }

  /**
   * F46 — enter inline-rename for a workspace header. The synthetic groups
   * (the empty-registry "__all__" fallback and the "__unassigned__" bucket)
   * are not real registry workspaces, so double-clicking them is a no-op.
   */
  startRenameWorkspace(g: ExplorerWorkspaceGroup): void {
    if (g.id === '__all__' || g.id === '__unassigned__') return;
    this.renameDraft.set(g.displayName);
    this.renamingWsId.set(g.id);
  }

  /**
   * Right-click "Rename" context menu on a workspace header. Double-click is
   * the fast path but undiscoverable, so the menu makes the same registry-only
   * rename visible. Viewport-relative coordinates feed `<app-menu>`'s absolute
   * positioning; synthetic groups have no real registry record to rename, so we
   * let the browser's native menu through for them instead.
   */
  readonly wsContextMenu = signal<{ id: string; x: number; y: number } | null>(null);

  readonly wsContextMenuItems = computed<readonly MenuItem[]>(() =>
    this.wsContextMenu() ? [{ kind: 'row', id: 'rename', label: 'Rename' }] : [],
  );

  readonly wsContextMenuPosition = computed(() => {
    const ctx = this.wsContextMenu();
    return ctx ? { x: ctx.x, y: ctx.y } : null;
  });

  openWsContextMenu(event: MouseEvent, g: ExplorerWorkspaceGroup): void {
    if (g.id === '__all__' || g.id === '__unassigned__') return;
    event.preventDefault();
    this.wsContextMenu.set({ id: g.id, x: event.clientX, y: event.clientY });
  }

  closeWsContextMenu(): void {
    this.wsContextMenu.set(null);
  }

  onWsContextMenuItemClick(ev: MenuItemClickEvent): void {
    const ctx = this.wsContextMenu();
    this.closeWsContextMenu();
    if (!ctx || ev.id !== 'rename') return;
    const g = this.groups().find(group => group.id === ctx.id);
    if (g) this.startRenameWorkspace(g);
  }

  cancelRename(): void {
    this.renamingWsId.set(null);
  }

  /** Enter commits, Escape cancels. Bound to the raw `keydown` rather than the
   *  `keydown.enter` / `keydown.escape` pseudo-events because the latter's
   *  Escape filter did not fire reliably for this input. */
  onRenameKeydown(ev: KeyboardEvent): void {
    if (ev.key === 'Enter') {
      ev.preventDefault();
      this.commitRename();
    } else if (ev.key === 'Escape') {
      ev.preventDefault();
      this.cancelRename();
    }
  }

  /**
   * Commit the draft name. No-op when blank or unchanged; otherwise emits
   * {@link renameWorkspace} for the shell to persist. Closing edit mode first
   * makes the blur that follows Enter/Escape a no-op (renamingWsId is null).
   */
  commitRename(): void {
    const id = this.renamingWsId();
    if (id === null) return;
    this.renamingWsId.set(null);
    const value = this.renameDraft().trim();
    const original = this.groups().find(g => g.id === id)?.displayName ?? '';
    if (!value || value === original) return;
    this.renameWorkspace.emit({ id, displayName: value });
  }

  /** True when dropping the dragged project on this workspace folder would
   *  actually move it (real workspace, not the source's current one). */
  canDropOnWorkspace(targetWorkspaceId: string): boolean {
    return this.projectDrag.canDropOnWorkspace(targetWorkspaceId);
  }

  /** Hover-title for a project row. Drop targets are workspace folders now,
   *  so the affordance points the user at the gesture rather than the row. */
  rowTitle(p: ExplorerProjectNode): string {
    return p.projectId
      ? 'Drag onto a workspace folder to move this project there'
      : 'This project is not in the registry yet — drag to move is unavailable';
  }

  onDragStart(event: DragEvent, p: ExplorerProjectNode): void {
    this.projectDrag.onDragStart(event, {
      projectId: p.projectId,
      name: p.name,
      workspaceId: p.workspaceId,
    });
  }

  onWorkspaceDragOver(event: DragEvent, g: ExplorerWorkspaceGroup): void {
    this.projectDrag.onWorkspaceDragOver(event, g.id);
  }

  /** Only clear the hover highlight when the pointer leaves the whole group
   *  wrapper — moving between the header and a child row fires dragleave on
   *  the inner element and would otherwise flicker the highlight off. */
  onWorkspaceDragLeave(event: DragEvent, g: ExplorerWorkspaceGroup): void {
    const related = event.relatedTarget as Node | null;
    const wrapper = event.currentTarget as HTMLElement | null;
    if (related && wrapper?.contains(related)) return;
    this.projectDrag.onWorkspaceDragLeave(g.id);
  }

  onWorkspaceDrop(event: DragEvent, g: ExplorerWorkspaceGroup): void {
    event.preventDefault();
    const projectId = this.projectDrag.draggingProjectId();
    const valid = !!projectId && this.projectDrag.canDropOnWorkspace(g.id);
    this.projectDrag.onDragEnd();
    if (valid && projectId) {
      this.projectDrop.emit({ projectId, targetWorkspaceId: g.id });
    }
  }

  onDragEnd(): void {
    this.projectDrag.onDragEnd();
  }
}
