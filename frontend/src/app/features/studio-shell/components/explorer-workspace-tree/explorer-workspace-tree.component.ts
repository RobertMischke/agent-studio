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
import type { RegistryWorkspaceListItem, RegistryProjectUrl } from '../../../../models/task.model';
import { ProjectUrlProbeService } from '../../../../services/project-url-probe.service';
import { ModalStackService } from '../../../../services/modal-stack.service';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { EmptyStateComponent } from '../../../../components/empty-state/empty-state.component';
import { SectionHeaderComponent } from '../../../../components/section-header/section-header.component';
import { TreeRowComponent } from '../../../../components/tree-row/tree-row.component';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { MenuComponent, type MenuItem, type MenuItemClickEvent } from '../../../../components/menu';
import { ProjectDragDropService } from '../../../shell';
import { ExplorerSectionsService } from '../../services/explorer-sections.service';
import { ExplorerProjectActionsService } from '../../services/explorer-project-actions.service';
import { boardLaneCountsLabel, laneCountsFor, type ExplorerLaneCounts } from '../../studio-shell.project-rows';
import { ExplorerLaneDashboardComponent, type ExplorerTreeMetricView } from '../explorer-lane-dashboard/explorer-lane-dashboard.component';
import {
  aggregatePulse,
  aggregatePulseTooltip,
  pulseAriaLabel,
  pulseTooltip,
  type ExplorerPulseAggregate,
  type ProjectPulseState,
} from '../../studio-shell.pulse';
import { ExplorerAutoPulseComponent } from '../explorer-auto-pulse/explorer-auto-pulse.component';

/** Flat project row as computed by the shell (`ProjectSidebarRow`). */
export interface ExplorerProjectRow {
  name: string;
  initial: string;
  color: string;
  totalJobs: number;
  laneCounts?: ExplorerLaneCounts;
  isActive: boolean;
}

/** A project row decorated with its registry metadata, ready to render. */
export interface ExplorerProjectNode extends ExplorerProjectRow {
  /** Registry id (PROJ-NNN) for matched rows; null for synthetic rows
   *  ("__all__" / "__unassigned__"), which are not draggable. */
  projectId: string | null;
  /** Owning registry workspace id for matched rows (rejects same-workspace
   *  drops); null when unmatched. */
  workspaceId: string | null;
  /** Registry `displayName` for matched rows (falls back to `name`); the row
   *  stays keyed by `name` so a rename never breaks grouping. */
  displayLabel: string;
  /** Registry short code for matched rows; the delete confirm accepts it. */
  shortCode: string | null;
  urls: readonly RegistryProjectUrl[]; // configured URLs → extra child rows
}

/** One workspace folder and the project rows that belong to it. */
export interface ExplorerWorkspaceGroup {
  id: string;
  displayName: string;
  color: string | null;
  projects: ExplorerProjectNode[];
}

export type ExplorerProjectSurface = 'board' | 'hub' | 'wiki' | 'epics';
function folderTail(path: string): string {
  const parts = path.split(/[\\/]+/).filter(Boolean);
  return parts.length ? parts[parts.length - 1] : path;
}

/** Canonicalise a storage path (unify slashes, drop trailing sep, lower-case)
 *  for the rename-stable WatchPath==storageLocation join key. */
function normalizeStorage(path: string): string {
  return path.replace(/[\\/]+/g, '/').replace(/\/+$/, '').toLowerCase();
}

/**
 * F46 — Explorer two-level workspace → project tree. Purely presentational
 * over the shell's flat project rows, joined to
 * {@link RegistryWorkspaceListItem}s by storage path / display name / folder
 * tail; unmatched rows fall into "Unassigned", and an empty registry falls
 * back to one legacy folder so the tree never goes blank. In-tree management
 * affordances (workspace + project rename, project delete) are surfaced at the
 * node via double-click / right-click; all edits are registry-only.
 */
@Component({
  selector: 'app-explorer-workspace-tree',
  standalone: true,
  imports: [SectionHeaderComponent, TreeRowComponent, StudioIconComponent, EmptyStateComponent, TooltipDirective, MenuComponent, ExplorerAutoPulseComponent, ExplorerLaneDashboardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './explorer-workspace-tree.component.html',
  styleUrl: './explorer-workspace-tree.component.scss',
})
export class ExplorerWorkspaceTreeComponent {
  readonly projectRows = input<readonly ExplorerProjectRow[]>([]);
  readonly registryWorkspaces = input<readonly RegistryWorkspaceListItem[]>([]);
  /** Row name → resolved storage path (from the host's WatchPaths). Lets the
   *  workspace→project join key on storage instead of the mutable display
   *  name, so a registry rename keeps the row under its workspace (F46 step 7). */
  readonly projectStorageByName = input<ReadonlyMap<string, string>>(new Map());
  readonly expandedProjects = input<ReadonlySet<string>>(new Set());
  readonly showAllActive = input(false);
  readonly activeProjectSurface = input<ExplorerProjectSurface | null>(null);
  /** Experimental active-work visualization. Numbers remain the default. */
  readonly metricView = input<ExplorerTreeMetricView>('numbers');
  /** AGT-2031 — project name → auto-pickup pulse state. Missing entries are
   *  treated as `off`. Feeds the subtle activity indicator on each project row
   *  and the aggregated pulse on collapsed workspace / tree nodes. */
  readonly projectPulseByName = input<ReadonlyMap<string, ProjectPulseState>>(new Map());

  readonly showAll = output<void>();
  readonly toggleExpanded = output<string>();
  readonly openBoardRequest = output<string>();
  readonly openHubRequest = output<string>();
  /** AGT-2067 — open a URL row's embedded preview tab (project + url id). */
  readonly openUrlPreviewRequest = output<{ projectName: string; urlId: string }>();
  /** Open the project's Project Hub deep-linked to its Wiki rail. */
  readonly openWikiRequest = output<string>();
  /** Project-scoped epic overview open for the named project (ASS-658). */
  readonly openEpicsRequest = output<string>();
  /** Open the project onboarding modal preselected to this workspace. */
  readonly onboardProjectRequest = output<string>();
  /** Open the create-workspace dialog from the Workspaces section header. */
  readonly onboardWorkspaceRequest = output<void>();
  /** Project row dropped onto a different real workspace; the shell PUTs
   *  /api/projects/{projectId} `{ workspaceId }` and reloads (no folder move). */
  readonly projectDrop = output<{ projectId: string; targetWorkspaceId: string }>();

  /** F46 — workspace-header inline rename committed from the editor; the shell
   *  PUTs /api/workspaces/{id} (registry metadata only, no folder move). */
  readonly renameWorkspace = output<{ id: string; displayName: string }>();

  /** F46 step 7 — registry-only project rename committed from the inline editor
   *  (shell PUTs `{ displayName }`; PROJ id + storage untouched). */
  readonly renameProject = output<{ projectId: string; displayName: string }>();

  /** F46 — destructive project delete from the right-click menu; the shell runs
   *  the two-stage typed confirm + DELETE. `shortCode` lets the confirm accept
   *  it as an alias for the display name. */
  readonly deleteProject = output<{ projectId: string; displayName: string; shortCode: string | null }>();

  readonly projectDrag = inject(ProjectDragDropService);
  readonly projectActions = inject(ExplorerProjectActionsService);
  /** Public so the template can read each URL row's live running/offline dot. */
  readonly urlProbe = inject(ProjectUrlProbeService);
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

  // Push the open rename input onto the modal stack so Escape cancels it:
  // always-mounted overlays keep the stack non-empty, so the input's own
  // (keydown) Escape would otherwise be swallowed by the stack top first.
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

  private readonly projectRenameInputRef = viewChild<ElementRef<HTMLInputElement>>('projectRenameInput');
  private readonly focusProjectRenameFx = effect(() => {
    if (this.projectActions.renamingProjectId() === null) return;
    const el = this.projectRenameInputRef()?.nativeElement;
    if (el) queueMicrotask(() => { el.focus(); el.select(); });
  });

  // Project-row rename: same modal-stack Escape arbitration as the workspace one.
  private projectRenameModalDispose: (() => void) | null = null;
  private readonly projectRenameModalFx = effect(() => {
    const renaming = this.projectActions.renamingProjectId() !== null;
    if (renaming && !this.projectRenameModalDispose) {
      this.projectRenameModalDispose = this.modalStack.push('explorer-project-rename', () => {
        this.projectActions.cancelRename();
        return true;
      });
    } else if (!renaming && this.projectRenameModalDispose) {
      this.projectRenameModalDispose();
      this.projectRenameModalDispose = null;
    }
  });

  constructor() {
    inject(DestroyRef).onDestroy(() => {
      this.renameModalDispose?.();
      this.projectRenameModalDispose?.();
    });
  }

  readonly totalProjectCount = computed(() => this.projectRows().length);

  readonly groups = computed<ExplorerWorkspaceGroup[]>(() => {
    const rows = this.projectRows();
    const storageByName = this.projectStorageByName();
    const node = (
      r: ExplorerProjectRow,
      projectId: string | null,
      workspaceId: string | null,
      displayLabel: string,
      shortCode: string | null,
      urls: readonly RegistryProjectUrl[] = [],
    ): ExplorerProjectNode => ({
      ...r,
      projectId,
      workspaceId,
      displayLabel,
      shortCode,
      urls,
    });

    const workspaces = [...this.registryWorkspaces()].sort((a, b) => a.sortOrder - b.sortOrder);
    if (workspaces.length === 0) {
      return rows.length
        ? [{ id: '__all__', displayName: 'Workspace', color: null, projects: rows.map(r => node(r, null, null, r.name, null)) }]
        : [];
    }

    const byName = new Map(rows.map(r => [r.name, r] as const));
    // Rename-stable join: a row's resolved storage path equals the registry
    // `storageLocation` and never changes on a display rename, so match on it
    // first. Fall back to the display-name / folder-tail joins for rows whose
    // storage the host did not supply (legacy callers pass an empty map).
    const byStorage = new Map<string, ExplorerProjectRow>();
    for (const r of rows) {
      const storage = storageByName.get(r.name);
      if (storage) byStorage.set(normalizeStorage(storage), r);
    }
    const used = new Set<string>();
    const groups: ExplorerWorkspaceGroup[] = [];
    for (const ws of workspaces) {
      const projects: ExplorerProjectNode[] = [];
      for (const rp of ws.projects) {
        const match =
          byStorage.get(normalizeStorage(rp.storageLocation)) ??
          byName.get(rp.displayName) ??
          byName.get(folderTail(rp.storageLocation));
        if (match && !used.has(match.name)) {
          used.add(match.name);
          // Carry the registry id + owning workspace so the drag source knows
          // what to reassign and which workspace drop is a no-op; carry the
          // registry display name + short code so the row renders the live
          // label and the delete confirm can accept the short code.
          projects.push(node(match, rp.id, ws.id, rp.displayName, rp.shortCode, rp.urls ?? []));
        }
      }
      projects.sort((a, b) => a.displayLabel.localeCompare(b.displayLabel));
      groups.push({ id: ws.id, displayName: ws.displayName, color: ws.color, projects });
    }

    const leftover = rows
      .filter(r => !used.has(r.name))
      .sort((a, b) => a.name.localeCompare(b.name))
      .map(r => node(r, null, null, r.name, null));
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

  // Toggle from the live service value, not the row's [chevron] input: a
  // double-click fires two clicks before the row re-renders, so both would read
  // the same stale value and collapse instead of netting back.
  onWsHeaderToggle(g: ExplorerWorkspaceGroup): void {
    const key = 'ws:' + g.id;
    this.setCollapsed(key, !this.isCollapsed(key));
  }

  isExpanded(name: string): boolean {
    return this.expandedProjects().has(name);
  }

  /** AGT-2067 — primary click on a URL row opens its embedded preview tab. */
  openUrlPreview(projectName: string, urlId: string): void {
    this.openUrlPreviewRequest.emit({ projectName, urlId });
  }

  /** Fallback escape hatch kept on the row: open the URL in a real browser tab. */
  openUrlExternal(url: string, event?: Event): void {
    event?.stopPropagation();
    window.open(url, '_blank', 'noopener');
  }

  readonly laneCountsFor = laneCountsFor;
  readonly boardLaneCountsLabel = boardLaneCountsLabel;

  // ── AGT-2031: auto-pickup pulse indicator (logic in studio-shell.pulse) ───

  /** Pulse state for a single project row (`off` when unknown / not on auto). */
  pulseStateFor(name: string): ProjectPulseState {
    return this.projectPulseByName().get(name) ?? 'off';
  }

  readonly pulseTooltip = pulseTooltip;
  readonly pulseAriaLabel = pulseAriaLabel;
  readonly aggregatePulseTooltip = aggregatePulseTooltip;

  /** Roll a workspace group's project pulses up into one aggregate. */
  wsPulseAggregate(g: ExplorerWorkspaceGroup): ExplorerPulseAggregate {
    return aggregatePulse(g.projects.map(p => p.name), this.projectPulseByName());
  }

  /** Whole-tree aggregate, shown on the panel header when the tree is collapsed. */
  readonly allPulseAggregate = computed<ExplorerPulseAggregate>(() => {
    const pulses = this.projectPulseByName();
    return aggregatePulse([...pulses.keys()], pulses);
  });

  /** Enter inline-rename for a real workspace header (synthetic groups no-op). */
  startRenameWorkspace(g: ExplorerWorkspaceGroup): void {
    if (g.id === '__all__' || g.id === '__unassigned__') return;
    this.renameDraft.set(g.displayName);
    this.renamingWsId.set(g.id);
  }

  /** Right-click "Rename" menu on a workspace header (viewport coords for
   *  `<app-menu>`); synthetic groups fall through to the native menu. */
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

  /** Right-click menu on a project row; only registry-backed rows (PROJ id)
   *  get it, unmatched rows fall through to the native menu. */
  onProjectContextMenu(event: MouseEvent, p: ExplorerProjectNode): void {
    if (!p.projectId) return;
    event.preventDefault();
    event.stopPropagation();
    this.projectActions.openContextMenu({
      projectId: p.projectId,
      name: p.name,
      displayName: p.displayLabel,
      shortCode: p.shortCode,
      x: event.clientX,
      y: event.clientY,
    });
  }

  onProjectContextMenuItemClick(ev: MenuItemClickEvent): void {
    const ctx = this.projectActions.contextMenu();
    this.projectActions.closeContextMenu();
    if (!ctx) return;
    if (ev.id === 'rename') {
      this.projectActions.startRename(ctx.projectId, ctx.displayName);
    } else if (ev.id === 'delete') {
      this.deleteProject.emit({ projectId: ctx.projectId, displayName: ctx.displayName, shortCode: ctx.shortCode });
    }
  }

  onProjectRenameKeydown(ev: KeyboardEvent): void {
    if (ev.key === 'Enter') {
      ev.preventDefault();
      this.commitRenameProject();
    } else if (ev.key === 'Escape') {
      ev.preventDefault();
      this.projectActions.cancelRename();
    }
  }

  /** Commit the inline rename, emitting {@link renameProject} on a real change. */
  commitRenameProject(): void {
    const projectId = this.projectActions.renamingProjectId();
    if (projectId === null) return;
    const current = this.findProjectNode(projectId)?.displayLabel ?? '';
    const result = this.projectActions.commitRename(current);
    if (result) this.renameProject.emit(result);
  }

  private findProjectNode(projectId: string): ExplorerProjectNode | undefined {
    for (const g of this.groups()) {
      const found = g.projects.find(p => p.projectId === projectId);
      if (found) return found;
    }
    return undefined;
  }

  cancelRename(): void {
    this.renamingWsId.set(null);
  }

  // Raw keydown (not keydown.enter/.escape pseudo-events: the Escape filter
  // did not fire reliably here). Enter commits, Escape cancels.
  onRenameKeydown(ev: KeyboardEvent): void {
    if (ev.key === 'Enter') {
      ev.preventDefault();
      this.commitRename();
    } else if (ev.key === 'Escape') {
      ev.preventDefault();
      this.cancelRename();
    }
  }

  /** Commit the draft name (no-op when blank/unchanged); closing edit mode
   *  first makes the blur that follows Enter/Escape a no-op. */
  commitRename(): void {
    const id = this.renamingWsId();
    if (id === null) return;
    this.renamingWsId.set(null);
    const value = this.renameDraft().trim();
    const original = this.groups().find(g => g.id === id)?.displayName ?? '';
    if (!value || value === original) return;
    this.renameWorkspace.emit({ id, displayName: value });
  }

  /** True when dropping the dragged project here would actually move it. */
  canDropOnWorkspace(targetWorkspaceId: string): boolean {
    return this.projectDrag.canDropOnWorkspace(targetWorkspaceId);
  }

  /** Hover-title for a project row pointing at the drag-to-move gesture. */
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

  // Clear the highlight only when leaving the whole wrapper, else moving
  // between header and child rows flickers it off.
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
