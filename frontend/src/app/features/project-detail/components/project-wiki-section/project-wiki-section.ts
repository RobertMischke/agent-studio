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
import { ProjectDocsService } from '../../../../services/project-docs.service';
import {
  WikiFileHistory,
  WikiNodeType,
  WikiTree,
  WikiTreeNode,
} from '../../../../models/project-docs.model';
import { MarkdownViewComponent } from '../../../../components/markdown-view/markdown-view.component';
import { MenuComponent } from '../../../../components/menu/menu.component';
import { MenuItem, MenuItemClickEvent } from '../../../../components/menu/menu.types';
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

/**
 * Project-level Wiki view backed by the physical docs/ folder hierarchy: the
 * tree is the real folders + .md/.html files on disk (no virtual organisation
 * layer). Folders expand/collapse; the right pane renders the selected document
 * (markdown inline, HTML inside a script-disabled sandboxed iframe) plus a
 * History tab with per-doc provenance, the file's git log, and old-revision
 * previews.
 *
 * Structural edits are real git commits in the project repo: a text-only
 * context menu offers New page / New folder / Rename / Delete, and dragging a
 * file onto a folder moves it (git mv). The tree re-reads from disk after every
 * mutation, so what you see is the committed state.
 */
@Component({
  selector: 'app-project-wiki-section',
  standalone: true,
  imports: [FormsModule, MarkdownViewComponent, MenuComponent, WikiDocHistoryComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-wiki-section.html',
  styleUrl: './project-wiki-section.scss',
})
export class ProjectWikiSectionComponent {
  readonly projectName = input.required<string>();

  private readonly docs = inject(ProjectDocsService);
  private readonly host = inject(ElementRef<HTMLElement>);
  private readonly sanitizer = inject(DomSanitizer);

  readonly tree = signal<WikiTree | null>(null);
  readonly loading = signal(false);
  readonly busy = signal(false);
  readonly filter = signal('');

  readonly expanded = signal<ReadonlySet<string>>(new Set());

  readonly openedRel = signal<string | null>(null);
  readonly openedType = signal<WikiNodeType>('md');
  readonly openedContent = signal<string>('');
  readonly loadingDoc = signal(false);
  readonly viewerTab = signal<'doc' | 'history'>('doc');
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

  private seeded = false;

  protected readonly nodeId = nodeId;

  constructor() {
    effect(() => {
      const p = this.projectName();
      if (p) this.refresh();
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
        { kind: 'row', id: 'new-folder', label: 'New folder' },
        { kind: 'row', id: 'rename', label: 'Rename' },
        { kind: 'separator' },
        { kind: 'row', id: 'delete', label: 'Delete folder', danger: true },
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

  // ---- loading ----

  refresh(): void {
    const p = this.projectName();
    if (!p) return;
    this.seeded = false;
    this.loading.set(true);
    this.docs.getWikiTree(p).subscribe({
      next: t => {
        this.tree.set(t);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
  }

  // ---- viewer ----

  openFile(rel: string, type: WikiNodeType = 'md', tab: 'doc' | 'history' = 'doc'): void {
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
  }

  closeFile(): void {
    this.openedRel.set(null);
    this.openedContent.set('');
    this.history.set(null);
    this.revisionSha.set(null);
    this.revisionContent.set('');
  }

  selectTab(tab: 'doc' | 'history'): void {
    this.viewerTab.set(tab);
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
      },
      error: () => { /* leave the current view untouched on failure */ },
    });
  }

  backToCurrent(): void {
    this.revisionSha.set(null);
    this.revisionContent.set('');
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
        if (t.relPath) this.openFile(t.relPath, t.type, 'history');
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
    const name = this.prompt('New folder name:', '');
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
}
