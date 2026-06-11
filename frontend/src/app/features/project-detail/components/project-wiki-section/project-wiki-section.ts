import {
  ChangeDetectionStrategy,
  Component,
  ElementRef,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { FormsModule } from '@angular/forms';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { DriftReport, DriftReportDetailResponse } from '../../../../models/drift.model';
import { ProjectDocsService } from '../../../../services/project-docs.service';
import { DriftService } from '../../../../services/drift.service';
import { TaskService } from '../../../../services/task.service';
import { CliCatalogStore } from '../../../../services/cli-catalog.store';
import { copyTextToClipboard } from '../../../../services/clipboard.util';
import { OverlayPortalDirective } from '../../../../directives/overlay-portal.directive';
import { TooltipDirective } from '../../../../components/tooltip';
import { CLI_TYPES, CliType, TaskState } from '../../../../models/task.model';
import {
  WikiFileHistory,
  WikiNodeType,
  WikiTree,
  WikiTreeNode,
} from '../../../../models/project-docs.model';
import { MarkdownViewComponent } from '../../../../components/markdown-view/markdown-view.component';
import { MenuComponent } from '../../../../components/menu/menu.component';
import { MenuItem, MenuItemClickEvent } from '../../../../components/menu/menu.types';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { resolveWikiImageSrc } from './wiki-image-resolver';
import { WikiDocHistoryComponent } from './wiki-doc-history/wiki-doc-history.component';
import {
  WikiTreeRow,
  collectFolderIds,
  filterWikiTree,
  flattenWikiTree,
  nodeId,
} from './wiki-tree';

const FILE_DRAG_TYPE = 'application/x-wiki-file';
const WIKI_STATE_STORAGE_PREFIX = 'atp.projectWiki.v1.';
const WIKI_NAV_MIN_WIDTH = 216;
const WIKI_NAV_MAX_WIDTH = 420;
const WIKI_NAV_DEFAULT_WIDTH = 286;
const WIKI_CONTEXT_MIN_WIDTH = 232;
const WIKI_CONTEXT_MAX_WIDTH = 420;
const WIKI_CONTEXT_DEFAULT_WIDTH = 284;
const WIKI_RESIZE_STEP = 16;

type WikiViewerTab = 'doc' | 'source';
type WikiResizablePanel = 'nav' | 'context';

interface WikiPersistedState {
  navCollapsed?: boolean;
  contextCollapsed?: boolean;
  openedRel?: string | null;
  viewerTab?: WikiViewerTab;
  navWidth?: number;
  contextWidth?: number;
}

interface WikiResizeState {
  panel: WikiResizablePanel;
  pointerId: number;
  startX: number;
  startWidth: number;
}

interface WikiDocLink {
  label: string;
  target: string;
  kind: 'doc' | 'anchor' | 'external';
}

/**
 * Project-level knowledge view backed by the physical docs/ folder hierarchy:
 * the tree is the real folders + .md/.html files on disk (no virtual
 * organisation layer). Categories expand/collapse; the right pane renders the
 * selected page (markdown inline, HTML inside a script-disabled sandboxed
 * iframe). The right context rail carries provenance, the file's git log, and
 * old-revision previews so only one page is open at a time.
 *
 * Structural edits are real git commits in the project repo: a text-only
 * context menu offers New page / New category / Rename / Delete, and dragging a
 * file onto a folder moves it (git mv). The tree re-reads from disk after every
 * mutation, so what you see is the committed state.
 */
@Component({
  selector: 'app-project-wiki-section',
  standalone: true,
  imports: [
    FormsModule,
    MarkdownViewComponent,
    MenuComponent,
    OverlayPortalDirective,
    StudioIconComponent,
    TooltipDirective,
    WikiDocHistoryComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-wiki-section.html',
  styleUrl: './project-wiki-section.scss',
})
export class ProjectWikiSectionComponent {
  readonly projectName = input.required<string>();

  private readonly docs = inject(ProjectDocsService);
  private readonly drift = inject(DriftService);
  private readonly tasks = inject(TaskService);
  private readonly catalog = inject(CliCatalogStore);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly sanitizer = inject(DomSanitizer);

  readonly cliTypes = CLI_TYPES;

  readonly tree = signal<WikiTree | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly filter = signal('');
  readonly filterOpen = signal(false);

  readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly navCollapsed = signal(false);
  readonly contextCollapsed = signal(false);
  readonly navWidth = signal(WIKI_NAV_DEFAULT_WIDTH);
  readonly contextWidth = signal(WIKI_CONTEXT_DEFAULT_WIDTH);
  readonly navWidthStyle = computed(() => `${this.navWidth()}px`);
  readonly contextWidthStyle = computed(() => `${this.contextWidth()}px`);
  readonly resizingPanel = signal<WikiResizablePanel | null>(null);

  readonly navMinWidth = WIKI_NAV_MIN_WIDTH;
  readonly navMaxWidth = WIKI_NAV_MAX_WIDTH;
  readonly contextMinWidth = WIKI_CONTEXT_MIN_WIDTH;
  readonly contextMaxWidth = WIKI_CONTEXT_MAX_WIDTH;

  readonly openedRel = signal<string | null>(null);
  readonly openedType = signal<WikiNodeType>('md');
  readonly openedContent = signal<string>('');
  readonly loadingDoc = signal(false);
  readonly viewerTab = signal<WikiViewerTab>('doc');
  readonly history = signal<WikiFileHistory | null>(null);
  readonly loadingHistory = signal(false);

  // Old-revision preview: when a sha is set, the doc pane shows that revision's
  // content instead of the working-tree content, with a "back to current" banner.
  readonly revisionSha = signal<string | null>(null);
  readonly revisionContent = signal<string>('');

  // Context menu + inline rename.
  readonly menuOpen = signal(false);
  readonly menuPos = signal<{ x: number; y: number } | null>(null);
  readonly menuTarget = signal<WikiTreeNode | null>(null);
  readonly renamingId = signal<string | null>(null);
  readonly renameValue = signal('');

  // Drag-and-drop (file onto folder).
  readonly draggingRel = signal<string | null>(null);
  readonly dropTargetId = signal<string | null>(null);

  readonly driftModalOpen = signal(false);
  readonly driftBusy = signal(false);
  readonly driftError = signal<string | null>(null);
  readonly driftMessage = signal<string | null>(null);
  readonly driftPrompt = signal('');
  readonly driftPromptLoading = signal(false);
  readonly driftReportDetail = signal<DriftReportDetailResponse | null>(null);
  readonly driftReports = signal<DriftReport[]>([]);
  readonly driftProjectKey = signal<string | null>(null);
  readonly driftWatchPath = signal<string | null>(null);
  readonly driftCli = signal<CliType>('claude');
  readonly driftModel = signal('');
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');

  private seeded = false;
  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;
  private pendingOpenRestore: { rel: string; tab: WikiViewerTab } | null = null;
  private resizeState: WikiResizeState | null = null;

  protected readonly nodeId = nodeId;

  constructor() {
    effect(() => {
      const p = this.projectName();
      if (p) {
        this.restorePersistedState(p);
        this.refresh();
      }
    });
    // Expand every folder the first time the tree loads so it opens fully.
    // Guarded so manual collapses survive later refreshes.
    effect(() => {
      const t = this.tree();
      if (!this.seeded && t) {
        this.expanded.set(new Set(collectFolderIds(t.root)));
        this.seeded = true;
      }
    });
  }

  readonly roots = computed<WikiTreeNode[]>(() => this.tree()?.root ?? []);

  readonly filteredRoots = computed<WikiTreeNode[]>(() =>
    filterWikiTree(this.roots(), this.filter()));

  readonly rows = computed<WikiTreeRow[]>(() => {
    const roots = this.filteredRoots();
    const exp = this.filter().trim() ? new Set(collectFolderIds(roots)) : this.expanded();
    return flattenWikiTree(roots, exp);
  });

  readonly docCount = computed(() => {
    let n = 0;
    const walk = (nodes: readonly WikiTreeNode[]): void => {
      for (const node of nodes) {
        if (node.type === 'folder') walk(node.children);
        else n++;
      }
    };
    walk(this.roots());
    return n;
  });

  readonly menuItems = computed<MenuItem[]>(() => {
    const t = this.menuTarget();
    if (!t) return [];
    if (t.type === 'folder') {
      return [
        { kind: 'row', id: 'new-page', label: 'New page' },
        { kind: 'row', id: 'new-folder', label: 'New category' },
        { kind: 'row', id: 'rename', label: 'Rename' },
        { kind: 'separator' },
        { kind: 'row', id: 'delete', label: 'Delete category', danger: true },
      ];
    }
    return [
      { kind: 'row', id: 'rename', label: 'Rename' },
      { kind: 'row', id: 'history', label: 'View history' },
      { kind: 'separator' },
      { kind: 'row', id: 'delete', label: 'Delete', danger: true },
    ];
  });

  /** Image resolver bound to the currently opened doc's folder (markdown only). */
  readonly imageResolver = computed<(src: string) => string>(() => {
    const project = this.projectName();
    const rel = this.openedRel();
    if (!rel) return (s: string) => s;
    return (s: string) => resolveWikiImageSrc(s, rel, a => this.docs.wikiAssetUrl(project, a));
  });

  /** Content shown in the doc pane: the previewed revision when one is active. */
  readonly displayContent = computed(() =>
    this.revisionSha() ? this.revisionContent() : this.openedContent());

  /** Trusted srcdoc for an HTML doc — the iframe sandbox (no scripts) isolates it. */
  readonly trustedHtml = computed<SafeHtml>(() =>
    this.sanitizer.bypassSecurityTrustHtml(this.displayContent()));

  /**
   * Latest commit for the open doc (history is newest-first), surfaced as the
   * doc-header "last modified" line: when + who + the commit subject (= why).
   */
  readonly lastCommit = computed(() => this.history()?.commits?.[0] ?? null);

  readonly openedNode = computed(() => {
    const rel = this.openedRel();
    return rel ? this.findNode(this.roots(), rel) : null;
  });

  readonly openedTitle = computed(() =>
    this.openedNode()?.title ?? this.basename(this.openedRel() ?? 'Document'));

  readonly openedFolder = computed(() => {
    const rel = this.openedRel();
    if (!rel) return '';
    return this.parentDir(rel) || 'root folder';
  });

  readonly openedKindLabel = computed(() => {
    switch (this.openedType()) {
      case 'html': return 'HTML';
      case 'md': return 'Markdown';
      default: return 'Page';
    }
  });

  readonly wordCount = computed(() => {
    const raw = this.displayContent()
      .replace(/```[\s\S]*?```/g, ' ')
      .replace(/<[^>]+>/g, ' ')
      .replace(/[#>*_`[\]()!-]/g, ' ');
    return raw.trim() ? raw.trim().split(/\s+/).length : 0;
  });

  readonly docLinks = computed<WikiDocLink[]>(() => this.extractLinks(this.displayContent()));

  readonly firstDoc = computed(() => this.findFirstDoc(this.roots()));

  readonly suggestedDocs = computed(() => this.collectDocs(this.roots()).slice(0, 6));

  readonly rootFolderLabel = computed(() => {
    const base = this.tree()?.baseDir?.trim();
    return base || 'root folder';
  });

  readonly modelOptions = computed(() => this.catalog.modelsFor(this.driftCli()));

  readonly selectedModelLabel = computed(() => {
    const id = this.driftModel();
    if (!id) return 'CLI default';
    return this.modelOptions().find(m => m.id === id)?.label ?? id;
  });

  readonly latestDriftReport = computed<DriftReport | null>(() =>
    this.driftReportDetail()?.report ?? this.driftReports()[0] ?? null);

  readonly driftModalText = computed(() => {
    const markdown = this.driftReportDetail()?.markdown;
    if (markdown?.trim()) return markdown;
    return this.buildDocumentDriftPrompt();
  });

  readonly driftCopyLabel = computed(() => {
    switch (this.copyState()) {
      case 'copied': return 'Copied';
      case 'failed': return 'Copy failed';
      default: return 'Copy result';
    }
  });

  // ---- loading ----

  refresh(): void {
    const p = this.projectName();
    if (!p) return;
    this.seeded = false;
    this.loading.set(true);
    this.docs.getWikiTree(p).subscribe({
      next: t => {
        this.tree.set(t);
        this.restorePendingOpen(t);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  // ---- viewer ----

  openFile(rel: string, type: WikiNodeType = 'md', tab: WikiViewerTab = 'doc'): void {
    this.openedRel.set(rel);
    this.openedType.set(type);
    this.viewerTab.set(tab);
    this.openedContent.set('');
    this.revisionSha.set(null);
    this.revisionContent.set('');
    this.loadingDoc.set(true);
    this.docs.getWikiFile(this.projectName(), rel).subscribe({
      next: r => {
        this.openedContent.set(r.content);
        this.loadingDoc.set(false);
      },
      error: () => {
        this.openedContent.set('(failed to load)');
        this.loadingDoc.set(false);
      },
    });
    this.history.set(null);
    this.loadingHistory.set(true);
    this.docs.getWikiFileHistory(this.projectName(), rel).subscribe({
      next: h => {
        this.history.set(h);
        this.loadingHistory.set(false);
      },
      error: () => this.loadingHistory.set(false),
    });
    this.persistState();
  }

  closeFile(): void {
    this.openedRel.set(null);
    this.openedContent.set('');
    this.history.set(null);
    this.revisionSha.set(null);
    this.revisionContent.set('');
    this.pendingOpenRestore = null;
    this.persistState();
  }

  selectTab(tab: WikiViewerTab): void {
    this.viewerTab.set(tab);
    this.persistState();
  }

  openFirstDoc(): void {
    const first = this.firstDoc();
    if (first?.relPath) this.openFile(first.relPath, first.type);
  }

  toggleFilter(): void {
    this.filterOpen.update(v => !v);
    queueMicrotask(() => {
      const root = this.host.nativeElement as HTMLElement;
      root.querySelector<HTMLInputElement>('[data-testid="project-wiki-filter"]')?.focus();
    });
  }

  toggleNav(): void {
    this.navCollapsed.update(v => !v);
    this.persistState();
  }

  toggleContext(): void {
    this.contextCollapsed.update(v => !v);
    this.persistState();
  }

  startPanelResize(event: PointerEvent, panel: WikiResizablePanel): void {
    event.preventDefault();
    const target = event.currentTarget as HTMLElement | null;
    target?.setPointerCapture?.(event.pointerId);
    this.resizeState = {
      panel,
      pointerId: event.pointerId,
      startX: event.clientX,
      startWidth: panel === 'nav' ? this.navWidth() : this.contextWidth(),
    };
    this.resizingPanel.set(panel);
  }

  resizePanel(event: PointerEvent): void {
    const state = this.resizeState;
    if (!state || event.pointerId !== state.pointerId) return;

    const delta = state.panel === 'nav'
      ? event.clientX - state.startX
      : state.startX - event.clientX;
    if (Math.abs(delta) <= 2) return;

    this.applyPanelWidth(state.panel, state.startWidth + delta);
  }

  finishPanelResize(event: PointerEvent): void {
    const state = this.resizeState;
    if (!state || event.pointerId !== state.pointerId) return;

    const target = event.currentTarget as HTMLElement | null;
    target?.releasePointerCapture?.(event.pointerId);
    this.resizeState = null;
    this.resizingPanel.set(null);
    this.persistState();
  }

  onPanelSplitterKeydown(event: KeyboardEvent, panel: WikiResizablePanel): void {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    event.preventDefault();

    const direction = event.key === 'ArrowRight' ? 1 : -1;
    const current = panel === 'nav' ? this.navWidth() : this.contextWidth();
    const next = panel === 'nav'
      ? current + (direction * WIKI_RESIZE_STEP)
      : current - (direction * WIKI_RESIZE_STEP);
    this.applyPanelWidth(panel, next);
    this.persistState();
  }

  onDriftCliChange(value: string): void {
    const next = CLI_TYPES.includes(value as CliType) ? value as CliType : 'claude';
    this.driftCli.set(next);
    this.driftModel.set('');
    this.catalog.ensure(next).subscribe({ error: () => void 0 });
  }

  onDriftModelChange(value: string): void {
    this.driftModel.set(value);
  }

  openDriftModal(): void {
    this.driftModalOpen.set(true);
    this.driftError.set(null);
    this.driftMessage.set(null);
    this.copyState.set('idle');
    this.catalog.ensure(this.driftCli()).subscribe({ error: () => void 0 });
    this.resolveDriftProjectContext(() => {
      this.loadDriftReports();
      this.loadDriftPrompt();
    });
  }

  closeDriftModal(ev?: Event): void {
    if (ev && ev.target && (ev.target as HTMLElement).closest('.pwiki__drift-card')) return;
    this.driftModalOpen.set(false);
  }

  stopModal(ev: Event): void {
    ev.stopPropagation();
  }

  prepareDriftEvidenceReport(): void {
    const project = this.driftProjectKey() ?? this.projectName();
    if (!project) return;
    this.driftBusy.set(true);
    this.driftError.set(null);
    this.driftMessage.set(null);
    this.drift.runSoftwareArchitectureDrift(project).subscribe({
      next: detail => {
        this.driftReportDetail.set(detail);
        this.driftReports.set([detail.report, ...this.driftReports().filter(r => r.reportId !== detail.report.reportId)]);
        this.driftBusy.set(false);
        this.driftMessage.set(`Evidence report ${detail.report.reportId} recorded.`);
      },
      error: err => {
        this.driftBusy.set(false);
        this.driftError.set(this.describeError(err, 'Could not prepare the drift evidence report.'));
      },
    });
  }

  startDriftCliTask(): void {
    const project = this.projectName();
    const watchPath = this.driftWatchPath();
    if (!project || !watchPath) {
      this.resolveDriftProjectContext(() => this.startDriftCliTask());
      return;
    }

    const cli = this.driftCli();
    const model = this.driftModel().trim();
    const docSlug = this.toSlug(this.openedRel() ?? 'wiki-index').slice(0, 36);
    const id = `wiki-drift-${docSlug}-${Date.now().toString(36)}`.slice(0, 96);
    this.driftBusy.set(true);
    this.driftError.set(null);
    this.driftMessage.set(null);

    this.tasks.createJob({
      id,
      title: `Knowledge drift: ${this.openedTitle()}`,
      agent: cli,
      cliType: cli,
      model: model || undefined,
      watchPath,
      targetState: TaskState.Ready,
      taskType: 'chore',
      promptMarkdown: this.buildDocumentDriftPrompt(),
    }).subscribe({
      next: created => {
        const jobId = created?.id ?? id;
        this.tasks.startJob(jobId, watchPath, model || undefined, cli).subscribe({
          next: () => {
            this.driftBusy.set(false);
            this.driftMessage.set(`Started ${jobId} with ${cli}${model ? ` / ${model}` : ''}.`);
          },
          error: err => {
            this.driftBusy.set(false);
            this.driftError.set(this.describeError(err, `Created ${jobId}, but could not start the CLI run.`));
          },
        });
      },
      error: err => {
        this.driftBusy.set(false);
        this.driftError.set(this.describeError(err, 'Could not create the drift CLI task.'));
      },
    });
  }

  copyDriftResult(): void {
    void copyTextToClipboard(this.driftModalText()).then(ok => {
      this.copyState.set(ok ? 'copied' : 'failed');
      if (this.copyResetTimer) clearTimeout(this.copyResetTimer);
      this.copyResetTimer = setTimeout(() => {
        this.copyState.set('idle');
        this.copyResetTimer = null;
      }, 1800);
    });
  }

  // ---- old-revision preview ----

  onViewRevision(sha: string): void {
    const rel = this.openedRel();
    if (!rel || !sha) return;
    this.docs.getWikiRevision(this.projectName(), sha, rel).subscribe({
      next: r => {
        this.revisionSha.set(sha);
        this.revisionContent.set(r.content);
        this.viewerTab.set('doc');
        this.persistState();
      },
      error: () => { /* leave the current view untouched on failure */ },
    });
  }

  backToCurrent(): void {
    this.revisionSha.set(null);
    this.revisionContent.set('');
    this.persistState();
  }

  // ---- tree expand/collapse ----

  toggleExpand(id: string): void {
    const next = new Set(this.expanded());
    if (next.has(id)) next.delete(id);
    else next.add(id);
    this.expanded.set(next);
  }

  private expand(id: string): void {
    if (this.expanded().has(id)) return;
    const next = new Set(this.expanded());
    next.add(id);
    this.expanded.set(next);
  }

  // ---- context menu ----

  onContextMenu(ev: MouseEvent, node: WikiTreeNode): void {
    ev.preventDefault();
    this.menuTarget.set(node);
    this.menuPos.set({ x: ev.clientX, y: ev.clientY });
    this.menuOpen.set(true);
  }

  closeMenu(): void {
    this.menuOpen.set(false);
    this.menuTarget.set(null);
  }

  onMenuItemClick(ev: MenuItemClickEvent): void {
    const t = this.menuTarget();
    if (!t) return;
    switch (ev.id) {
      case 'rename':
        this.startRename(t);
        break;
      case 'history':
        if (t.relPath) {
          this.contextCollapsed.set(false);
          this.openFile(t.relPath, t.type);
        }
        break;
      case 'new-page':
        this.promptNewPage(t.relPath ?? '');
        break;
      case 'new-folder':
        this.promptNewFolder(t.relPath ?? '');
        break;
      case 'delete':
        this.deleteNode(t);
        break;
    }
    this.closeMenu();
  }

  // ---- create page / folder ----

  promptNewPage(folderRel: string): void {
    const name = this.prompt('New page name (e.g. guide.md):', '');
    if (name) this.createPage(folderRel, name);
  }

  promptNewFolder(folderRel: string): void {
    const name = this.prompt('New category name:', '');
    if (name) this.createFolder(folderRel, name);
  }

  /** Creates a page under a folder, defaulting to .md when no extension is given. */
  createPage(folderRel: string, name: string): void {
    const trimmed = name.trim();
    if (!trimmed) return;
    const withExt = /\.(md|html?|htm)$/i.test(trimmed) ? trimmed : `${trimmed}.md`;
    const rel = this.joinRel(folderRel, withExt);
    this.runMutation(this.docs.createWikiPage(this.projectName(), rel));
  }

  createFolder(folderRel: string, name: string): void {
    const trimmed = name.trim();
    if (!trimmed) return;
    const rel = this.joinRel(folderRel, trimmed);
    this.runMutation(this.docs.createWikiFolder(this.projectName(), rel));
  }

  // ---- rename (inline) ----

  startRename(node: WikiTreeNode): void {
    this.renamingId.set(nodeId(node));
    this.renameValue.set(node.name);
    queueMicrotask(() => {
      const root = this.host.nativeElement as HTMLElement;
      const el = root.querySelector<HTMLInputElement>('.pwiki__rename-input');
      el?.focus();
      el?.select();
    });
  }

  commitRename(): void {
    const id = this.renamingId();
    if (!id) return;
    const name = this.renameValue().trim();
    this.renamingId.set(null);
    if (!name || name === this.basename(id)) return;
    const target = this.findNode(this.roots(), id);
    if (!target?.relPath) return;
    const newRel = this.joinRel(this.parentDir(target.relPath), name);
    this.runMutation(this.docs.moveWikiNode(this.projectName(), target.relPath, newRel));
  }

  cancelRename(): void {
    this.renamingId.set(null);
  }

  // ---- delete ----

  private deleteNode(node: WikiTreeNode): void {
    if (!node.relPath) return;
    const ok = this.confirm(`Delete "${node.name}"? This is committed to the repository.`);
    if (!ok) return;
    if (this.openedRel() === node.relPath) this.closeFile();
    this.runMutation(this.docs.deleteWikiNode(this.projectName(), node.relPath));
  }

  // ---- drag and drop (file onto folder → git mv into the folder) ----

  onFileDragStart(ev: DragEvent, node: WikiTreeNode): void {
    if (node.type === 'folder' || !node.relPath || !ev.dataTransfer) return;
    ev.dataTransfer.setData(FILE_DRAG_TYPE, node.relPath);
    ev.dataTransfer.setData('text/plain', node.relPath);
    ev.dataTransfer.effectAllowed = 'move';
    this.draggingRel.set(node.relPath);
  }

  onDragEnd(): void {
    this.draggingRel.set(null);
    this.dropTargetId.set(null);
  }

  onFolderDragOver(ev: DragEvent, folder: WikiTreeNode): void {
    if (folder.type !== 'folder' || !this.draggingRel()) return;
    ev.preventDefault();
    if (ev.dataTransfer) ev.dataTransfer.dropEffect = 'move';
    this.dropTargetId.set(nodeId(folder));
  }

  onFolderDragLeave(folder: WikiTreeNode): void {
    if (folder.type !== 'folder') return;
    if (this.dropTargetId() === nodeId(folder)) this.dropTargetId.set(null);
  }

  onFolderDrop(ev: DragEvent, folder: WikiTreeNode): void {
    if (folder.type !== 'folder' || !folder.relPath) return;
    ev.preventDefault();
    const rel = ev.dataTransfer?.getData(FILE_DRAG_TYPE) || this.draggingRel();
    this.dropTargetId.set(null);
    this.draggingRel.set(null);
    if (!rel) return;
    const name = this.basename(rel);
    const dest = this.joinRel(folder.relPath, name);
    if (dest === rel) return;
    this.runMutation(this.docs.moveWikiNode(this.projectName(), rel, dest));
  }

  // ---- mutation plumbing ----

  private runMutation(obs: { subscribe: (o: { next: () => void; error: () => void }) => unknown }): void {
    this.busy.set(true);
    obs.subscribe({
      next: () => {
        this.busy.set(false);
        this.refresh();
      },
      error: () => {
        this.busy.set(false);
        this.refresh();
      },
    });
  }

  // ---- persisted workspace state ----

  private restorePersistedState(projectName: string): void {
    const state = this.readPersistedState(projectName);
    this.navCollapsed.set(state?.navCollapsed === true);
    this.contextCollapsed.set(state?.contextCollapsed === true);
    this.navWidth.set(this.clampNavWidth(state?.navWidth ?? WIKI_NAV_DEFAULT_WIDTH));
    this.contextWidth.set(this.clampContextWidth(state?.contextWidth ?? WIKI_CONTEXT_DEFAULT_WIDTH));
    this.viewerTab.set(this.safeViewerTab(state?.viewerTab));
    this.openedRel.set(null);
    this.openedContent.set('');
    this.history.set(null);
    this.revisionSha.set(null);
    this.revisionContent.set('');
    this.pendingOpenRestore = state?.openedRel
      ? { rel: state.openedRel, tab: this.safeViewerTab(state.viewerTab) }
      : null;
  }

  private restorePendingOpen(tree: WikiTree): void {
    const pending = this.pendingOpenRestore;
    if (!pending) return;
    this.pendingOpenRestore = null;
    const node = this.findNode(tree.root, pending.rel);
    if (!node || node.type === 'folder' || !node.relPath) {
      this.persistState();
      return;
    }
    this.openFile(node.relPath, node.type, pending.tab);
  }

  private persistState(): void {
    const projectName = this.projectName();
    if (!projectName) return;
    const state: WikiPersistedState = {
      navCollapsed: this.navCollapsed(),
      contextCollapsed: this.contextCollapsed(),
      openedRel: this.openedRel(),
      viewerTab: this.viewerTab(),
      navWidth: this.navWidth(),
      contextWidth: this.contextWidth(),
    };
    this.writePersistedState(projectName, state);
  }

  private readPersistedState(projectName: string): WikiPersistedState | null {
    try {
      const raw = globalThis.localStorage?.getItem(this.storageKey(projectName));
      if (!raw) return null;
      const parsed = JSON.parse(raw) as Partial<WikiPersistedState>;
      return {
        navCollapsed: parsed.navCollapsed === true,
        contextCollapsed: parsed.contextCollapsed === true,
        openedRel: typeof parsed.openedRel === 'string' && parsed.openedRel.trim() ? parsed.openedRel : null,
        viewerTab: this.safeViewerTab(parsed.viewerTab),
        navWidth: this.readStoredWidth(parsed.navWidth, WIKI_NAV_MIN_WIDTH, WIKI_NAV_MAX_WIDTH),
        contextWidth: this.readStoredWidth(parsed.contextWidth, WIKI_CONTEXT_MIN_WIDTH, WIKI_CONTEXT_MAX_WIDTH),
      };
    } catch {
      return null;
    }
  }

  private writePersistedState(projectName: string, state: WikiPersistedState): void {
    try {
      globalThis.localStorage?.setItem(this.storageKey(projectName), JSON.stringify(state));
    } catch {
      /* persistence is a convenience; the wiki keeps working without storage */
    }
  }

  private storageKey(projectName: string): string {
    return `${WIKI_STATE_STORAGE_PREFIX}${encodeURIComponent(projectName)}`;
  }

  private safeViewerTab(value: unknown): WikiViewerTab {
    return value === 'source' || value === 'doc' ? value : 'doc';
  }

  private applyPanelWidth(panel: WikiResizablePanel, width: number): void {
    if (panel === 'nav') {
      this.navWidth.set(this.clampNavWidth(width));
      return;
    }
    this.contextWidth.set(this.clampContextWidth(width));
  }

  private clampNavWidth(width: number): number {
    return this.clampWidth(width, WIKI_NAV_MIN_WIDTH, WIKI_NAV_MAX_WIDTH);
  }

  private clampContextWidth(width: number): number {
    return this.clampWidth(width, WIKI_CONTEXT_MIN_WIDTH, WIKI_CONTEXT_MAX_WIDTH);
  }

  private clampWidth(width: number, min: number, max: number): number {
    return Math.min(max, Math.max(min, Math.round(width)));
  }

  private readStoredWidth(value: unknown, min: number, max: number): number | undefined {
    if (typeof value !== 'number' || !Number.isFinite(value)) return undefined;
    return this.clampWidth(value, min, max);
  }

  private resolveDriftProjectContext(after: () => void): void {
    const project = this.projectName();
    this.tasks.getWatchPaths().subscribe({
      next: entries => {
        const match = entries.find(e => e.name === project)
          ?? entries.find(e => e.path === project);
        this.driftWatchPath.set(match?.path ?? project);
        this.driftProjectKey.set(match?.path ? this.basename(match.path.replace(/\\/g, '/')) : project);
        after();
      },
      error: () => {
        this.driftWatchPath.set(project);
        this.driftProjectKey.set(project);
        after();
      },
    });
  }

  private loadDriftReports(): void {
    const project = this.driftProjectKey() ?? this.projectName();
    if (!project) return;
    this.drift.listReports(project, { limit: 12 }).subscribe({
      next: resp => this.driftReports.set(resp?.reports ?? []),
      error: () => this.driftReports.set([]),
    });
  }

  private loadDriftPrompt(): void {
    const project = this.driftProjectKey() ?? this.projectName();
    if (!project) return;
    this.driftPromptLoading.set(true);
    this.drift.getSoftwareArchitectureDriftPrompt(project).subscribe({
      next: resp => {
        this.driftPrompt.set(resp.prompt ?? '');
        this.driftPromptLoading.set(false);
      },
      error: err => {
        this.driftPrompt.set('');
        this.driftPromptLoading.set(false);
        this.driftError.set(this.describeError(err, 'Could not load the architecture drift prompt.'));
      },
    });
  }

  private buildDocumentDriftPrompt(): string {
    const rel = this.openedRel() ?? '(root folder)';
    const title = this.openedRel() ? this.openedTitle() : 'Root folder';
    const report = this.latestDriftReport();
    const basePrompt = this.driftPrompt().trim();
    const linked = this.docLinks().map(link => `- ${this.linkKindLabel(link.kind)}: ${link.label} (${link.target})`).join('\n');
    const model = this.driftModel().trim() || 'CLI default';

    return `# Knowledge page drift analysis: ${title}

Project: ${this.projectName()}
Page: ${rel}
Category: ${this.openedRel() ? this.openedFolder() : 'root folder'}
Page type: ${this.openedRel() ? this.openedKindLabel() : 'Root folder'}
Selected CLI: ${this.driftCli()}
Selected model: ${model}
Latest known drift report: ${report ? `${report.reportId} (${this.formatTimestamp(report.createdAt)})` : 'none loaded'}

## Objective

Evaluate whether the selected knowledge page still matches the current project architecture, source tree, task evidence, and concept notes.

## Linked elements visible in the Knowledge UI

${linked || '- No explicit Markdown or HTML links detected in the selected page.'}

## Required output

1. Produce a concise human-readable drift report.
2. Classify the result as Healthy, Watch, Warn, Critical, or Unknown.
3. List evidence refs using repository-relative paths.
4. Identify whether the page needs edits, a follow-up task, or no action.
5. Create or update a conceptual page-metadata note for this page. Suggested sidecar path:
   \`docs/wiki/.drift/${this.toSlug(rel)}.md\`
6. If the result should become a project drift report, post the structured response back through:
   \`POST /api/drift/{project}/actions/software-architecture-drift\`

## Base architecture-drift prompt

${basePrompt || '(Prompt not loaded yet. Use the project architecture model, docs, source tree, schemas, tests, recent tasks, and recent drift reports as evidence.)'}
`;
  }

  // ---- path + node helpers ----

  private parentDir(rel: string): string {
    const i = rel.lastIndexOf('/');
    return i >= 0 ? rel.slice(0, i) : '';
  }

  private basename(rel: string): string {
    const i = rel.lastIndexOf('/');
    return i >= 0 ? rel.slice(i + 1) : rel;
  }

  private joinRel(dir: string, name: string): string {
    return dir ? `${dir}/${name}` : name;
  }

  private findNode(nodes: readonly WikiTreeNode[], id: string): WikiTreeNode | null {
    for (const n of nodes) {
      if (nodeId(n) === id) return n;
      const hit = this.findNode(n.children, id);
      if (hit) return hit;
    }
    return null;
  }

  private findFirstDoc(nodes: readonly WikiTreeNode[]): WikiTreeNode | null {
    for (const node of nodes) {
      if (node.type !== 'folder') return node;
      const hit = this.findFirstDoc(node.children);
      if (hit) return hit;
    }
    return null;
  }

  private collectDocs(nodes: readonly WikiTreeNode[]): WikiTreeNode[] {
    const docs: WikiTreeNode[] = [];
    const walk = (items: readonly WikiTreeNode[]): void => {
      for (const node of items) {
        if (node.type === 'folder') walk(node.children);
        else docs.push(node);
      }
    };
    walk(nodes);
    return docs;
  }

  private extractLinks(content: string): WikiDocLink[] {
    const links: WikiDocLink[] = [];
    const seen = new Set<string>();
    const push = (label: string, target: string): void => {
      const cleanTarget = target.trim();
      if (!cleanTarget || cleanTarget.startsWith('mailto:')) return;
      const key = `${label}\u0000${cleanTarget}`;
      if (seen.has(key)) return;
      seen.add(key);
      links.push({
        label: label.trim() || cleanTarget,
        target: cleanTarget,
        kind: this.linkKind(cleanTarget),
      });
    };

    const markdownLink = /(!)?\[([^\]]+)\]\(([^)\s]+)(?:\s+"[^"]*")?\)/g;
    for (const match of content.matchAll(markdownLink)) {
      if (match[1]) continue;
      push(match[2] ?? '', match[3] ?? '');
    }

    const htmlLink = /<a\s+[^>]*href=["']([^"']+)["'][^>]*>(.*?)<\/a>/gis;
    for (const match of content.matchAll(htmlLink)) {
      push((match[2] ?? '').replace(/<[^>]+>/g, '').trim(), match[1] ?? '');
    }

    return links.slice(0, 8);
  }

  private linkKind(target: string): WikiDocLink['kind'] {
    if (target.startsWith('#')) return 'anchor';
    if (/^[a-z][a-z0-9+.-]*:/i.test(target)) return 'external';
    return 'doc';
  }

  linkKindLabel(kind: WikiDocLink['kind']): string {
    switch (kind) {
      case 'anchor': return 'Anchor';
      case 'external': return 'External';
      default: return 'Doc';
    }
  }

  navPath(node: WikiTreeNode): string {
    const rel = node.relPath ?? '';
    if (!rel) return 'root folder';
    const parent = this.parentDir(rel);
    return parent || 'root folder';
  }

  fileTypeLabel(node: WikiTreeNode): string {
    return node.type === 'html' ? 'HTML page' : 'Markdown page';
  }

  private toSlug(value: string): string {
    return value.trim().toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '') || 'document';
  }

  private describeError(err: unknown, fallback: string): string {
    if (!err) return fallback;
    const e = err as { error?: { error?: string }; message?: string };
    return e.error?.error ?? e.message ?? fallback;
  }

  // Seams so unit tests can drive the create/rename/delete flows without the
  // browser dialogs that back the menu UX.
  protected prompt(message: string, value: string): string | null {
    return globalThis.prompt?.(message, value) ?? null;
  }

  protected confirm(message: string): boolean {
    return globalThis.confirm?.(message) ?? true;
  }

  // ---- presentation helpers ----

  rowPad(depth: number): number {
    return 6 + depth * 16;
  }

  /** Locale date-time for the doc-header last-modified line; blank on bad input. */
  formatTimestamp(iso: string | null | undefined): string {
    if (!iso) return '';
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
  }
}
