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
import { ProjectDocsService } from '../../../../services/project-docs.service';
import {
  WikiFileEntry,
  WikiFileHistory,
  WikiOrgNode,
  WikiOrganization,
  WikiOverview,
} from '../../../../models/project-docs.model';
import { MarkdownViewComponent } from '../../../../components/markdown-view/markdown-view.component';
import { MenuComponent } from '../../../../components/menu/menu.component';
import { MenuItem, MenuItemClickEvent } from '../../../../components/menu/menu.types';
import { resolveWikiImageSrc } from './wiki-image-resolver';
import { WikiDocHistoryComponent } from './wiki-doc-history/wiki-doc-history.component';
import {
  WikiTreeNode,
  buildWikiTree,
  collectGroupIds,
  docId,
  flattenWikiTree,
  pruneEmptyGroups,
} from './wiki-tree';

const DOC_DRAG_TYPE = 'application/x-wiki-doc';

/**
 * Project-level Wiki view. The flat folder list was replaced by a user-
 * organisable tree: a virtual organisation manifest (docs/.wiki-organization
 * .json) layers themes (groups), nesting, ordering, and doc title overrides
 * over the immutable docs/ tree, so the underlying files — and their git
 * history — stay untouched. Anything the manifest does not place shows under a
 * synthetic "Ungrouped" bucket.
 *
 * Interactions: expand/collapse groups, drag a doc onto a group to move it,
 * right-click for a text-only context menu (rename / new subgroup / delete /
 * remove-from-group), and a "New group" button. The right pane renders the
 * selected document and a History tab with per-doc provenance (which model
 * touched it when / why) plus the file's git log.
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

  readonly overview = signal<WikiOverview | null>(null);
  readonly org = signal<WikiOrganization | null>(null);
  readonly loading = signal(false);
  readonly savingOrg = signal(false);
  readonly filter = signal('');

  readonly expanded = signal<ReadonlySet<string>>(new Set());

  readonly openedRel = signal<string | null>(null);
  readonly openedContent = signal<string>('');
  readonly loadingDoc = signal(false);
  readonly viewerTab = signal<'doc' | 'history'>('doc');
  readonly history = signal<WikiFileHistory | null>(null);
  readonly loadingHistory = signal(false);

  // Context menu + inline rename.
  readonly menuOpen = signal(false);
  readonly menuPos = signal<{ x: number; y: number } | null>(null);
  readonly menuTarget = signal<WikiTreeNode | null>(null);
  readonly renamingId = signal<string | null>(null);
  readonly renameValue = signal('');

  // Drag-and-drop.
  readonly draggingRel = signal<string | null>(null);
  readonly dropTargetId = signal<string | null>(null);

  private seeded = false;

  constructor() {
    effect(() => {
      const p = this.projectName();
      if (p) this.refresh();
    });
    // Seed every group expanded the first time both sources are loaded so the
    // tree opens fully (matching the old always-flat view). Guarded so manual
    // collapses survive subsequent manifest saves.
    effect(() => {
      const ov = this.overview();
      const org = this.org();
      if (!this.seeded && ov && org) {
        this.expanded.set(new Set(collectGroupIds(buildWikiTree(ov.files, org))));
        this.seeded = true;
      }
    });
  }

  readonly filteredFiles = computed<WikiFileEntry[]>(() => {
    const files = this.overview()?.files ?? [];
    const needle = this.filter().trim().toLowerCase();
    if (!needle) return files;
    return files.filter(f =>
      f.relPath.toLowerCase().includes(needle) || f.title.toLowerCase().includes(needle));
  });

  readonly tree = computed<WikiTreeNode[]>(() => {
    const roots = buildWikiTree(this.filteredFiles(), this.org());
    return this.filter().trim() ? pruneEmptyGroups(roots) : roots;
  });

  readonly rows = computed(() => {
    const tree = this.tree();
    const exp = this.filter().trim() ? new Set(collectGroupIds(tree)) : this.expanded();
    return flattenWikiTree(tree, exp);
  });

  readonly docCount = computed(() => this.overview()?.files.length ?? 0);

  readonly menuItems = computed<MenuItem[]>(() => {
    const t = this.menuTarget();
    if (!t) return [];
    if (t.kind === 'doc') {
      const items: MenuItem[] = [
        { kind: 'row', id: 'rename', label: 'Rename' },
        { kind: 'row', id: 'history', label: 'View history' },
      ];
      if (t.relPath && this.isPinned(t.relPath)) {
        items.push({ kind: 'separator' });
        items.push({ kind: 'row', id: 'unpin', label: 'Remove from group' });
      }
      return items;
    }
    if (t.synthetic) return [];
    return [
      { kind: 'row', id: 'rename', label: 'Rename' },
      { kind: 'row', id: 'subgroup', label: 'New subgroup' },
      { kind: 'separator' },
      { kind: 'row', id: 'delete', label: 'Delete group', danger: true },
    ];
  });

  /** Image resolver bound to the currently opened doc's folder. */
  readonly imageResolver = computed<(src: string) => string>(() => {
    const project = this.projectName();
    const rel = this.openedRel();
    if (!rel) return (s: string) => s;
    return (s: string) => resolveWikiImageSrc(s, rel, a => this.docs.wikiAssetUrl(project, a));
  });

  // ---- loading ----

  refresh(): void {
    const p = this.projectName();
    if (!p) return;
    this.seeded = false;
    this.loading.set(true);
    this.docs.getWikiOverview(p).subscribe({
      next: ov => {
        this.overview.set(ov);
        this.loading.set(false);
      },
      error: () => this.loading.set(false),
    });
    this.loadOrg();
  }

  private loadOrg(): void {
    const p = this.projectName();
    if (!p) return;
    this.docs.getWikiOrganization(p).subscribe({
      next: org => this.org.set(org),
      error: () => this.org.set({ version: 1, nodes: [] }),
    });
  }

  // ---- viewer ----

  openFile(rel: string, tab: 'doc' | 'history' = 'doc'): void {
    this.openedRel.set(rel);
    this.viewerTab.set(tab);
    this.openedContent.set('');
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
  }

  selectTab(tab: 'doc' | 'history'): void {
    this.viewerTab.set(tab);
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
    if (node.kind === 'group' && node.synthetic) return;
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
        if (t.relPath) this.openFile(t.relPath, 'history');
        break;
      case 'unpin':
        if (t.relPath) this.unpinDoc(t.relPath);
        break;
      case 'subgroup':
        this.createGroup(t.id);
        break;
      case 'delete':
        this.deleteGroup(t.id);
        break;
    }
    this.closeMenu();
  }

  // ---- rename (inline) ----

  startRename(node: WikiTreeNode): void {
    this.renamingId.set(node.id);
    this.renameValue.set(node.title);
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
    const title = this.renameValue().trim();
    this.renamingId.set(null);
    if (!title) return;
    if (id.startsWith('doc:')) {
      const rel = id.slice(4);
      this.mutateOrg(nodes => {
        const existing = nodes.find(n => n.type === 'doc' && n.relPath === rel);
        if (existing) existing.title = title;
        else nodes.push({ id, type: 'doc', title, relPath: rel, parentId: null, order: this.nextOrder(nodes, null) });
        return nodes;
      });
    } else {
      this.mutateOrg(nodes => {
        const g = nodes.find(n => n.type === 'group' && n.id === id);
        if (g) g.title = title;
        return nodes;
      });
    }
  }

  cancelRename(): void {
    this.renamingId.set(null);
  }

  // ---- group ops ----

  createGroup(parentId: string | null = null): void {
    const id = this.newId();
    this.mutateOrg(nodes => {
      nodes.push({ id, type: 'group', title: 'New group', relPath: null, parentId, order: this.nextOrder(nodes, parentId) });
      return nodes;
    });
    if (parentId) this.expand(parentId);
    this.expand(id);
    this.startRename({ id, kind: 'group', title: 'New group', relPath: null, synthetic: false, children: [] });
  }

  private deleteGroup(id: string): void {
    this.mutateOrg(nodes => {
      const target = nodes.find(n => n.type === 'group' && n.id === id);
      const reparent = target?.parentId ?? null;
      return nodes
        .filter(n => n.id !== id)
        .map(n => (n.parentId === id ? { ...n, parentId: reparent } : n));
    });
  }

  private unpinDoc(rel: string): void {
    this.mutateOrg(nodes => nodes.filter(n => !(n.type === 'doc' && n.relPath === rel)));
  }

  // ---- drag and drop ----

  onDocDragStart(ev: DragEvent, node: WikiTreeNode): void {
    if (!node.relPath || !ev.dataTransfer) return;
    ev.dataTransfer.setData(DOC_DRAG_TYPE, node.relPath);
    ev.dataTransfer.setData('text/plain', node.relPath);
    ev.dataTransfer.effectAllowed = 'move';
    this.draggingRel.set(node.relPath);
  }

  onDragEnd(): void {
    this.draggingRel.set(null);
    this.dropTargetId.set(null);
  }

  onGroupDragOver(ev: DragEvent, group: WikiTreeNode): void {
    if (group.kind !== 'group' || !this.draggingRel()) return;
    ev.preventDefault();
    if (ev.dataTransfer) ev.dataTransfer.dropEffect = 'move';
    this.dropTargetId.set(group.id);
  }

  onGroupDragLeave(group: WikiTreeNode): void {
    if (group.kind !== 'group') return;
    if (this.dropTargetId() === group.id) this.dropTargetId.set(null);
  }

  onGroupDrop(ev: DragEvent, group: WikiTreeNode): void {
    if (group.kind !== 'group') return;
    ev.preventDefault();
    const rel = ev.dataTransfer?.getData(DOC_DRAG_TYPE) || this.draggingRel();
    this.dropTargetId.set(null);
    this.draggingRel.set(null);
    if (!rel) return;
    // Dropping onto the synthetic Ungrouped bucket unpins the doc; dropping
    // onto a real group moves it there.
    if (group.synthetic) this.unpinDoc(rel);
    else this.moveDocToGroup(rel, group.id);
  }

  private moveDocToGroup(rel: string, groupId: string): void {
    this.mutateOrg(nodes => {
      const existing = nodes.find(n => n.type === 'doc' && n.relPath === rel);
      if (existing) {
        existing.parentId = groupId;
        existing.order = this.nextOrder(nodes.filter(n => n !== existing), groupId);
      } else {
        nodes.push({ id: docId(rel), type: 'doc', title: null, relPath: rel, parentId: groupId, order: this.nextOrder(nodes, groupId) });
      }
      return nodes;
    });
    this.expand(groupId);
  }

  // ---- manifest persistence ----

  private mutateOrg(fn: (nodes: WikiOrgNode[]) => WikiOrgNode[]): void {
    const current = this.org() ?? { version: 1, nodes: [] };
    const nodes = fn(current.nodes.map(n => ({ ...n })));
    const next: WikiOrganization = { version: current.version, nodes };
    this.org.set(next); // optimistic
    this.savingOrg.set(true);
    this.docs.putWikiOrganization(this.projectName(), next).subscribe({
      next: saved => {
        this.org.set(saved);
        this.savingOrg.set(false);
      },
      error: () => {
        this.savingOrg.set(false);
        this.loadOrg();
      },
    });
  }

  private isPinned(rel: string): boolean {
    return (this.org()?.nodes ?? []).some(n => n.type === 'doc' && n.relPath === rel);
  }

  private nextOrder(nodes: readonly WikiOrgNode[], parentId: string | null): number {
    let max = -1;
    for (const n of nodes) {
      if ((n.parentId ?? null) === (parentId ?? null) && n.order > max) max = n.order;
    }
    return max + 1;
  }

  private newId(): string {
    const c = globalThis.crypto;
    if (c && typeof c.randomUUID === 'function') return `g-${c.randomUUID()}`;
    return `g-${Math.random().toString(36).slice(2)}${Date.now().toString(36)}`;
  }

  // ---- presentation helpers ----

  rowPad(depth: number): number {
    return 6 + depth * 16;
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try { return new Date(iso).toLocaleDateString(); } catch { return iso; }
  }
}
