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
  WikiFileSaveResult,
  WikiFileHistory,
  WikiNodeType,
  WikiRecentEdit,
  WikiTree,
  WikiTreeMetadata,
  WikiTreeNode,
} from '../../../../models/project-docs.model';
import { MarkdownViewComponent } from '../../../../components/markdown-view/markdown-view.component';
import { MarkdownRichEditorComponent } from '../../../../components/markdown-rich-editor/markdown-rich-editor';
import { MenuComponent } from '../../../../components/menu/menu.component';
import { MenuItem, MenuItemClickEvent } from '../../../../components/menu/menu.types';
import { StudioIconComponent, type StudioIconName } from '../../../../components/studio-icon/studio-icon.component';
import { resolveWikiImageSrc } from './wiki-image-resolver';
import { WikiDocHistoryComponent } from './wiki-doc-history/wiki-doc-history.component';
import {
  WikiDashboardComponent,
  WikiDashboardDriftRow,
  WikiDashboardOpenRequest,
  WikiDashboardRecentRow,
} from './wiki-dashboard/wiki-dashboard.component';
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

type WikiViewerTab = 'doc' | 'report' | 'source' | 'edit';
type WikiResizablePanel = 'nav' | 'context';

interface WikiPersistedState {
  navCollapsed?: boolean;
  contextCollapsed?: boolean;
  openedRel?: string | null;
  viewerTab?: WikiViewerTab;
  navWidth?: number;
  contextWidth?: number;
  expandedIds?: string[];
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

type WikiMetricTone = 'good' | 'info' | 'warn' | 'bad' | 'muted';

interface WikiMetricChip {
  key: string;
  icon: StudioIconName;
  display: string;
  label: string;
  tone: WikiMetricTone;
  tooltip: string;
  reportAnchor: string | null;
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
    MarkdownRichEditorComponent,
    MenuComponent,
    OverlayPortalDirective,
    StudioIconComponent,
    TooltipDirective,
    WikiDashboardComponent,
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
  readonly recentEdits = signal<WikiRecentEdit[]>([]);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly filter = signal('');
  readonly filterOpen = signal(false);

  readonly expanded = signal<ReadonlySet<string>>(new Set());
  readonly focusedRowId = signal<string | null>(null);
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
  readonly reportContent = signal('');
  readonly reportAnchor = signal<string | null>(null);
  readonly reportError = signal<string | null>(null);
  readonly loadingReport = signal(false);
  readonly saveBusy = signal(false);
  readonly saveError = signal<string | null>(null);
  readonly saveResult = signal<WikiFileSaveResult | null>(null);
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

  private copyResetTimer: ReturnType<typeof setTimeout> | null = null;
  private pendingOpenRestore: { rel: string; tab: WikiViewerTab } | null = null;
  private loadedReportPath: string | null = null;
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

  readonly trustedReportHtml = computed<SafeHtml>(() =>
    this.sanitizer.bypassSecurityTrustHtml(this.reportHtmlForAnchor(
      this.reportContent(),
      this.reportAnchor())));

  /** Pretty JSON preview for metadata files; invalid JSON falls back to source. */
  readonly displayJson = computed(() => this.formatJson(this.displayContent()));

  /**
   * Latest commit for the open doc (history is newest-first), surfaced as the
   * doc-header "last modified" line: when + who + the commit subject (= why).
   */
  readonly lastCommit = computed(() => this.history()?.commits?.[0] ?? null);

  readonly openedNode = computed(() => {
    const rel = this.openedRel();
    return rel ? this.findNode(this.roots(), rel) : null;
  });

  readonly reportPath = computed(() => this.openedNode()?.metadata?.reportPath ?? null);

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
      case 'json': return 'JSON';
      case 'md': return 'Markdown';
      default: return 'Page';
    }
  });

  readonly canEditDoc = computed(() =>
    this.openedType() === 'md' && !this.revisionSha());

  readonly editDisabledReason = computed(() => {
    if (this.revisionSha()) return 'Old revisions are read-only.';
    if (this.openedType() !== 'md') return 'Rich editing is currently available for Markdown pages.';
    return null;
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

  // Dashboard: recently-edited rows (git author + time). Titles are taken from
  // the tree node when the path still resolves there (matches the nav label),
  // else from the git-reported title.
  readonly dashboardRecentRows = computed<WikiDashboardRecentRow[]>(() => {
    const byPath = new Map(this.collectDocs(this.roots()).map(d => [d.relPath, d]));
    return this.recentEdits().map(e => {
      const node = e.relPath ? byPath.get(e.relPath) : undefined;
      return {
        relPath: e.relPath,
        title: node?.title || e.title || e.relPath,
        author: e.author,
        authorDateUtc: e.authorDateUtc,
        type: node?.type ?? this.typeFromRelPath(e.relPath),
      };
    });
  });

  // Dashboard: pages a companion sidecar flags as drifting, worst first (grade
  // D, then by descending drift score). Derived entirely from tree metadata -
  // no extra fetch - so it stays in sync with the tree's own drift chips.
  readonly dashboardDriftRows = computed<WikiDashboardDriftRow[]>(() => {
    const rows = this.collectDocs(this.roots())
      .filter(d => d.relPath && d.metadata?.hasDrift === true)
      .map(d => {
        const meta = d.metadata!;
        const grade = this.cleanGrade(meta.driftGrade);
        return {
          relPath: d.relPath!,
          title: d.title,
          type: d.type,
          grade,
          score: meta.driftScore ?? null,
          tone: (grade === 'D' ? 'bad' : 'warn') as 'warn' | 'bad',
          summary: meta.summary?.trim() || null,
        };
      });
    rows.sort((a, b) => this.driftRank(b) - this.driftRank(a));
    return rows.slice(0, 8);
  });

  /** Sort key for high-drift rows: grade D outranks others, then drift score. */
  private driftRank(row: WikiDashboardDriftRow): number {
    const gradeWeight = row.grade === 'D' ? 4 : row.grade === 'C' ? 3 : row.grade === 'B' ? 2 : row.grade === 'A' ? 1 : 0;
    return gradeWeight * 1000 + Math.round((row.score ?? 0) * 100);
  }

  private typeFromRelPath(relPath: string): WikiNodeType {
    const ext = relPath.toLowerCase().split('.').pop() ?? '';
    if (ext === 'html' || ext === 'htm') return 'html';
    if (ext === 'json') return 'json';
    return 'md';
  }

  onDashboardOpen(req: WikiDashboardOpenRequest): void {
    this.openFile(req.relPath, req.type);
  }

  onDashboardOpenDrift(req: WikiDashboardOpenRequest): void {
    this.openFile(req.relPath, req.type, 'report', 'why-drift');
  }

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
    this.loading.set(true);
    this.docs.getWikiTree(p).subscribe({
      next: t => {
        this.tree.set(t);
        this.restorePendingOpen(t);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
    // Recent edits are git-backed and only feed the landing dashboard, so they
    // load independently and never block the tree render.
    this.docs.getWikiRecentEdits(p, 8).subscribe({
      next: r => this.recentEdits.set(r.edits ?? []),
      error: () => this.recentEdits.set([]),
    });
  }

  // ---- viewer ----

  openFile(rel: string, type: WikiNodeType = 'md', tab: WikiViewerTab = 'doc', reportAnchor: string | null = null): void {
    this.expandAncestors(rel);
    this.focusedRowId.set(rel);
    this.openedRel.set(rel);
    this.openedType.set(type);
    this.viewerTab.set(tab);
    this.openedContent.set('');
    this.reportContent.set('');
    this.reportAnchor.set(reportAnchor);
    this.reportError.set(null);
    this.loadingReport.set(false);
    this.loadedReportPath = null;
    this.saveError.set(null);
    this.saveResult.set(null);
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
    if (tab === 'report') this.loadReport();
    this.persistState();
  }

  closeFile(): void {
    this.openedRel.set(null);
    this.openedContent.set('');
    this.reportContent.set('');
    this.reportAnchor.set(null);
    this.reportError.set(null);
    this.loadingReport.set(false);
    this.loadedReportPath = null;
    this.saveError.set(null);
    this.saveResult.set(null);
    this.history.set(null);
    this.revisionSha.set(null);
    this.revisionContent.set('');
    this.pendingOpenRestore = null;
    this.persistState();
  }

  selectTab(tab: WikiViewerTab): void {
    if (tab === 'edit' && !this.canEditDoc()) return;
    this.viewerTab.set(tab);
    if (tab === 'report') this.loadReport();
    this.persistState();
  }

  openReportSection(event: Event, node: WikiTreeNode, anchor: string | null): void {
    event.preventDefault();
    event.stopPropagation();
    if (!node.relPath || !anchor) return;
    this.reportAnchor.set(anchor);
    if (this.openedRel() === node.relPath) {
      this.viewerTab.set('report');
      this.loadReport();
      this.persistState();
      return;
    }
    this.openFile(node.relPath, node.type, 'report', anchor);
  }

  saveWikiContent(content: string): void {
    const rel = this.openedRel();
    if (!rel || this.openedType() !== 'md') return;
    this.saveBusy.set(true);
    this.saveError.set(null);
    this.saveResult.set(null);
    this.docs.putWikiFile(this.projectName(), rel, content).subscribe({
      next: result => {
        this.saveBusy.set(false);
        this.saveResult.set(result);
        this.openedContent.set(content);
        this.reloadHistory(rel);
        this.refresh();
      },
      error: err => {
        this.saveBusy.set(false);
        this.saveError.set(err?.error?.error ?? 'Saving failed.');
      },
    });
  }

  private loadReport(): void {
    const path = this.reportPath();
    if (!path) {
      this.reportContent.set('');
      this.reportError.set('No reasoning report is linked to this document yet.');
      this.loadingReport.set(false);
      this.loadedReportPath = null;
      return;
    }
    if (this.loadedReportPath === path && this.reportContent()) return;

    this.loadedReportPath = path;
    this.reportContent.set('');
    this.reportError.set(null);
    this.loadingReport.set(true);
    this.docs.getWikiFile(this.projectName(), path).subscribe({
      next: r => {
        this.reportContent.set(r.content);
        this.loadingReport.set(false);
      },
      error: () => {
        this.reportError.set('Failed to load the linked reasoning report.');
        this.loadingReport.set(false);
      },
    });
  }

  private reloadHistory(rel: string): void {
    this.history.set(null);
    this.loadingHistory.set(true);
    this.docs.getWikiFileHistory(this.projectName(), rel).subscribe({
      next: h => {
        this.history.set(h);
        this.loadingHistory.set(false);
      },
      error: () => this.loadingHistory.set(false),
    });
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
    this.focusedRowId.set(id);
    this.persistState();
  }

  private expand(id: string): void {
    if (this.expanded().has(id)) return;
    const next = new Set(this.expanded());
    next.add(id);
    this.expanded.set(next);
    this.persistState();
  }

  focusTreeRow(node: WikiTreeNode): void {
    this.focusedRowId.set(nodeId(node));
  }

  focusTreeRowElement(event: MouseEvent, node: WikiTreeNode): void {
    this.focusedRowId.set(nodeId(node));
    const row = event.currentTarget as HTMLElement | null;
    if (!row) return;
    queueMicrotask(() => row.focus());
  }

  rowTabIndex(row: WikiTreeRow, index: number): number {
    const id = nodeId(row.node);
    const focused = this.focusedRowId();
    if (focused) return focused === id ? 0 : -1;
    const active = this.openedRel();
    if (active && active === id) return 0;
    return index === 0 ? 0 : -1;
  }

  treeItemExpanded(row: WikiTreeRow): boolean | null {
    return row.node.type === 'folder' && row.hasChildren ? row.expanded : null;
  }

  onTreeKeydown(event: KeyboardEvent): void {
    const rows = this.rows();
    if (rows.length === 0) return;

    const currentIndex = this.currentTreeRowIndex(event, rows);
    const row = rows[currentIndex];
    if (!row) return;

    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.focusRowAt(Math.min(currentIndex + 1, rows.length - 1));
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.focusRowAt(Math.max(currentIndex - 1, 0));
        break;
      case 'ArrowRight':
        event.preventDefault();
        if (row.node.type === 'folder' && row.hasChildren) {
          if (!row.expanded) {
            this.expand(nodeId(row.node));
            this.focusRowAt(currentIndex);
          } else {
            this.focusRowAt(Math.min(currentIndex + 1, this.rows().length - 1));
          }
        }
        break;
      case 'ArrowLeft':
        event.preventDefault();
        if (row.node.type === 'folder' && row.expanded) {
          this.toggleExpand(nodeId(row.node));
          this.focusRowAt(currentIndex);
        } else {
          const parent = this.parentRowIndex(rows, currentIndex);
          if (parent >= 0) this.focusRowAt(parent);
        }
        break;
      case 'Home':
        event.preventDefault();
        this.focusRowAt(0);
        break;
      case 'End':
        event.preventDefault();
        this.focusRowAt(rows.length - 1);
        break;
      case 'Enter':
      case ' ':
        event.preventDefault();
        this.activateTreeRow(row);
        break;
    }
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
    this.expanded.set(new Set(state?.expandedIds ?? []));
    this.focusedRowId.set(state?.openedRel ?? null);
    this.viewerTab.set(this.safeViewerTab(state?.viewerTab));
    this.openedRel.set(null);
    this.openedContent.set('');
    this.reportContent.set('');
    this.reportAnchor.set(null);
    this.reportError.set(null);
    this.loadingReport.set(false);
    this.loadedReportPath = null;
    this.saveError.set(null);
    this.saveResult.set(null);
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
      expandedIds: [...this.expanded()],
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
        expandedIds: this.readStoredExpandedIds(parsed.expandedIds),
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
    return value === 'source' || value === 'doc' || value === 'report' || value === 'edit'
      ? value
      : 'doc';
  }

  private reportHtmlForAnchor(html: string, anchor: string | null): string {
    const cleanAnchor = this.safeReportAnchor(anchor);
    if (!cleanAnchor || !html) return html;
    const injection = `<meta http-equiv="refresh" content="0;url=#${cleanAnchor}">`
      + `<style>:target{outline:2px solid #2563eb;outline-offset:6px;scroll-margin-top:18px;}</style>`;
    if (/<head[^>]*>/i.test(html)) {
      return html.replace(/<head([^>]*)>/i, `<head$1>${injection}`);
    }
    return injection + html;
  }

  private safeReportAnchor(anchor: string | null): string | null {
    if (!anchor) return null;
    const clean = anchor.trim().toLowerCase();
    return /^[a-z0-9-]+$/.test(clean) ? clean : null;
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

  private readStoredExpandedIds(value: unknown): string[] | undefined {
    if (!Array.isArray(value)) return undefined;
    return value.filter((item): item is string => typeof item === 'string' && item.trim().length > 0);
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

  private expandAncestors(rel: string): void {
    const parts = rel.split('/');
    if (parts.length <= 1) return;
    const next = new Set(this.expanded());
    for (let i = 1; i < parts.length; i++) {
      next.add(parts.slice(0, i).join('/'));
    }
    this.expanded.set(next);
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

  documentMetricChips(node: WikiTreeNode): WikiMetricChip[] {
    const meta = node.metadata ?? null;
    if (!meta) {
      return [
        {
          key: 'unscored',
          icon: 'file',
          display: 'None',
          label: 'Metadata unscored',
          tone: 'muted',
          tooltip: 'No adjacent companion metadata file describes this document yet.',
          reportAnchor: null,
        },
      ];
    }

    const chips = [
      this.driftChip(meta),
      this.directionChip(meta.temporalState),
    ].filter((chip): chip is WikiMetricChip => chip !== null);
    return chips;
  }

  private driftChip(meta: WikiTreeMetadata): WikiMetricChip {
    const cleanGrade = this.cleanGrade(meta.driftGrade);
    const summary = this.companionTooltipSummary(meta);
    if (meta.hasDrift === false) {
      return {
        key: 'drift',
        icon: 'check',
        display: cleanGrade ?? 'A',
        label: cleanGrade ? `Drift ${cleanGrade}` : 'Drift stable',
        tone: 'good',
        tooltip: this.joinTooltip('No drift is currently suspected.', summary),
        reportAnchor: 'why-drift',
      };
    }
    if (meta.hasDrift === true) {
      return {
        key: 'drift',
        icon: 'diff',
        display: cleanGrade ?? '?',
        label: cleanGrade ? `Drift ${cleanGrade}` : 'Drift unknown grade',
        tone: cleanGrade === 'D' ? 'bad' : 'warn',
        tooltip: this.joinTooltip('Drift is suspected for this document.', summary),
        reportAnchor: 'why-drift',
      };
    }
    return {
      key: 'drift',
      icon: 'diff',
      display: cleanGrade ?? '?',
      label: cleanGrade ? `Drift ${cleanGrade}` : 'Drift unknown',
      tone: 'muted',
      tooltip: this.joinTooltip('Drift state is not classified yet.', summary),
      reportAnchor: 'why-drift',
    };
  }

  private directionChip(state: string | null): WikiMetricChip {
    const normalized = this.normalizeMetric(state);
    switch (normalized) {
      case 'present':
      case 'current':
      case 'now':
        return {
          key: 'direction',
          icon: 'activity',
          display: 'Now',
          label: 'Direction Current',
          tone: 'muted',
          tooltip: 'Direction: describes current behavior.',
          reportAnchor: 'temporal-reasoning',
        };
      case 'future':
      case 'planned':
      case 'vision':
        return {
          key: 'direction',
          icon: 'branch',
          display: 'Fut',
          label: 'Direction Future',
          tone: 'muted',
          tooltip: 'Direction: describes planned or future behavior.',
          reportAnchor: 'temporal-reasoning',
        };
      case 'past':
      case 'historic':
      case 'obsolete':
        return {
          key: 'direction',
          icon: 'archive',
          display: 'Past',
          label: 'Direction Past',
          tone: 'muted',
          tooltip: 'Direction: describes past or obsolete behavior.',
          reportAnchor: 'temporal-reasoning',
        };
      case 'mixed':
      case 'transition':
        return {
          key: 'direction',
          icon: 'diff',
          display: 'Mix',
          label: 'Direction Mixed',
          tone: 'muted',
          tooltip: 'Direction: mixes current and planned behavior.',
          reportAnchor: 'temporal-reasoning',
        };
      default:
        return {
          key: 'direction',
          icon: 'activity',
          display: '?',
          label: 'Direction unknown',
          tone: 'muted',
          tooltip: 'Direction has not been classified yet.',
          reportAnchor: 'temporal-reasoning',
        };
    }
  }

  private cleanGrade(grade: string | null): string | null {
    const clean = grade?.trim().toUpperCase();
    return clean && /^[A-D]$/.test(clean) ? clean : null;
  }

  private normalizeMetric(value: string | null): string {
    return value?.trim().toLowerCase() ?? '';
  }

  private joinTooltip(primary: string, summary: string | null): string {
    const clean = summary?.trim();
    return clean ? `${primary} ${clean}` : primary;
  }

  private companionTooltipSummary(meta: WikiTreeMetadata): string | null {
    const parts: string[] = [];
    if (meta.sourceChangedSinceReview === true) {
      parts.push('Source changed since the companion review.');
    }
    if (meta.summary?.trim()) parts.push(meta.summary.trim());
    if (meta.findingsCount && meta.findingsCount > 0) {
      parts.push(`${meta.findingsCount} finding${meta.findingsCount === 1 ? '' : 's'} in the companion report.`);
    }
    if (meta.companionPath?.trim()) parts.push(`Companion: ${meta.companionPath.trim()}.`);
    return parts.length ? parts.join(' ') : null;
  }

  fileTypeLabel(node: WikiTreeNode): string {
    switch (node.type) {
      case 'html': return 'HTML page';
      case 'json': return 'JSON metadata';
      default: return 'Markdown page';
    }
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

  private currentTreeRowIndex(event: KeyboardEvent, rows: readonly WikiTreeRow[]): number {
    const target = event.target as HTMLElement | null;
    const rowEl = target?.closest?.('.pwiki__row') as HTMLElement | null;
    const domId = rowEl?.dataset['wikiRowId'];
    const id = domId || this.focusedRowId() || this.openedRel();
    const index = id ? rows.findIndex(r => nodeId(r.node) === id) : -1;
    return index >= 0 ? index : 0;
  }

  private focusRowAt(index: number): void {
    const rows = this.rows();
    const row = rows[index];
    if (!row) return;
    this.focusedRowId.set(nodeId(row.node));
    queueMicrotask(() => {
      const root = this.host.nativeElement as HTMLElement;
      root.querySelectorAll<HTMLElement>('.pwiki__row')[index]?.focus();
    });
  }

  private activateTreeRow(row: WikiTreeRow): void {
    if (row.node.type === 'folder') {
      this.toggleExpand(nodeId(row.node));
      return;
    }
    if (row.node.relPath) this.openFile(row.node.relPath, row.node.type);
  }

  private parentRowIndex(rows: readonly WikiTreeRow[], index: number): number {
    const depth = rows[index]?.depth ?? 0;
    if (depth <= 0) return -1;
    for (let i = index - 1; i >= 0; i--) {
      if (rows[i].depth === depth - 1) return i;
    }
    return -1;
  }

  private formatJson(content: string): string {
    try {
      return JSON.stringify(JSON.parse(content), null, 2);
    } catch {
      return content;
    }
  }

  /** Locale date-time for the doc-header last-modified line; blank on bad input. */
  formatTimestamp(iso: string | null | undefined): string {
    if (!iso) return '';
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? iso : d.toLocaleString();
  }
}
