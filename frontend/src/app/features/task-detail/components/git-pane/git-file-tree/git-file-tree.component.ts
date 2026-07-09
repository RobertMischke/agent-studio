import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import type { GitFileChange } from '../../../../../features/git';

import { TooltipDirective } from 'coding-agent-chat/shared';
interface TreeNode {
  /** Full path from repo root for files; folder path with trailing names joined for directories. */
  path: string;
  /** Display label (filename, or folded folder chain like "src/app/components"). */
  label: string;
  isFile: boolean;
  status: string;
  added: number;
  removed: number;
  /** Aggregated child count for folders (1 for files). */
  count: number;
  children: TreeNode[];
  /** Depth from the visual root, used only for indentation. */
  depth: number;
}

/**
 * Renders the changed-file list as a directory tree. The list of files is
 * still the source of truth; this component just folds shared path
 * prefixes so a user reviewing a task can scan the change set the way
 * they think about it (by folder).
 *
 * Folder chains with a single child are folded into one row
 * ("src/app/components") to avoid eating horizontal space on deep narrow
 * trees. Per-folder +/- aggregates are summed once at build time so the
 * template stays trivially OnPush.
 *
 * Expansion state is held in a signal keyed by node path; folders are
 * default-expanded when the total file count is small enough that the
 * tree fits without scrolling.
 */
@Component({
  selector: 'app-git-file-tree',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './git-file-tree.component.html',
  styleUrl: './git-file-tree.component.scss',
  host: {
    // Reflects the `fill` input onto the host so the SCSS selector
    // `:host([data-fill="true"]) .git-tree` actually matches. Without this
    // the tree stays capped at max-height: 30vh in the pane-maximized
    // split layout and leaves a tall empty column under the file list,
    // which also prevents the diff-col flex chain from resolving its full
    // height — the diff container appears stunted at ~30vh.
    '[attr.data-fill]': 'fill() ? "true" : null',
  },
})
export class GitFileTreeComponent {
  readonly files = input.required<readonly GitFileChange[]>();
  readonly selected = input<string | null>(null);
  /** Stretch the tree to fill the parent height (used in pane-maximized split layout). */
  readonly fill = input<boolean>(false);
  readonly selectRequest = output<string>();

  /** Path of the row that currently owns keyboard focus (roving tabindex). */
  private readonly activePath = signal<string | null>(null);

  readonly tree = computed<TreeNode[]>(() => buildTree(this.files()));

  /**
   * Basenames that appear on more than one changed file (e.g. two
   * `README.md` in different folders). Drives the visible directory hint +
   * makes the collision case obvious at a glance. Pure derivation off
   * `files()`; the full path is always available via {@link fileTooltip}.
   */
  private readonly collidingNames = computed<Set<string>>(() => {
    const counts = new Map<string, number>();
    for (const f of this.files()) {
      const name = baseName(f.path);
      if (!name) continue;
      counts.set(name, (counts.get(name) ?? 0) + 1);
    }
    const out = new Set<string>();
    for (const [name, n] of counts) if (n > 1) out.add(name);
    return out;
  });

  /** Full repo-relative path for a file row's hover tooltip. */
  fileTooltip(node: TreeNode): string {
    return node.path;
  }

  /**
   * Compact directory disambiguator shown only when a file's basename
   * collides with another changed file. Uses the immediate parent folder
   * ("docs/") - or "root" for a top-level file - so the two `README.md`
   * rows read as distinct without the full path eating the row width.
   * Returns '' when the name is unique (no hint needed).
   */
  dirHint(node: TreeNode): string {
    if (!node.isFile) return '';
    if (!this.collidingNames().has(baseName(node.path))) return '';
    const slash = node.path.lastIndexOf('/');
    if (slash <= 0) return 'root';
    const parent = node.path.slice(0, slash);
    const parentSlash = parent.lastIndexOf('/');
    return `${parentSlash >= 0 ? parent.slice(parentSlash + 1) : parent}/`;
  }

  /**
   * Default expansion: small change sets fully expand so the tree is
   * immediately scannable; large change sets start collapsed so the tree
   * does not flood the viewport. Pure derivation — never written.
   */
  private readonly defaultExpanded = computed<Set<string>>(() => {
    const tree = this.tree();
    const next = new Set<string>();
    if (countFiles(tree) <= 50) collectFolderPaths(tree, next);
    return next;
  });

  /**
   * User overrides to the default expansion, tagged by the `files()`
   * reference they were authored against. When `files()` changes the tag
   * stops matching and we revert to defaults — same reset behaviour as
   * the prior signal-write-on-load logic, but as a pure derivation.
   */
  private readonly userExpanded = signal<{
    files: readonly GitFileChange[];
    set: Set<string>;
  } | null>(null);

  readonly expanded = computed<Set<string>>(() => {
    const user = this.userExpanded();
    if (user && user.files === this.files()) return user.set;
    return this.defaultExpanded();
  });

  readonly visibleRows = computed<TreeNode[]>(() => {
    const out: TreeNode[] = [];
    walkVisible(this.tree(), this.expanded(), out);
    return out;
  });

  /**
   * The single row that is reachable by Tab (roving tabindex). Prefers the
   * row the user last interacted with, then the selected file, then the
   * first visible row so the tree is always focusable from the keyboard.
   */
  readonly rovingPath = computed<string | null>(() => {
    const rows = this.visibleRows();
    if (rows.length === 0) return null;
    const active = this.activePath();
    if (active && rows.some((r) => r.path === active)) return active;
    const sel = this.selected();
    if (sel && rows.some((r) => r.path === sel)) return sel;
    return rows[0].path;
  });

  ariaLevel(node: TreeNode): number {
    return node.depth + 1;
  }

  onClick(node: TreeNode): void {
    this.activePath.set(node.path);
    if (node.isFile) {
      this.selectRequest.emit(node.path);
      return;
    }
    this.setExpanded(node.path, !this.expanded().has(node.path));
  }

  /**
   * Keyboard navigation for the file tree (roving tabindex + ARIA `tree`).
   * Bound on the `<ul>`, so it only runs while focus is inside the tree and
   * never competes with the global scroll / shortcut handling. ↑/↓ move the
   * selection through the visible rows; landing on a file loads its diff on
   * the right, matching the click behaviour. ←/→ collapse / expand folders.
   */
  onKeydown(event: KeyboardEvent): void {
    const rows = this.visibleRows();
    if (rows.length === 0) return;
    const tree = event.currentTarget as HTMLElement;
    let index = rows.findIndex((r) => r.path === this.rovingPath());
    if (index < 0) index = 0;
    const node = rows[index];
    switch (event.key) {
      case 'ArrowDown':
        event.preventDefault();
        this.focusRowAt(Math.min(index + 1, rows.length - 1), rows, tree);
        break;
      case 'ArrowUp':
        event.preventDefault();
        this.focusRowAt(Math.max(index - 1, 0), rows, tree);
        break;
      case 'ArrowRight':
        event.preventDefault();
        if (!node.isFile) {
          if (!this.expanded().has(node.path)) this.setExpanded(node.path, true);
          else if (node.children.length) this.focusRowAt(index + 1, rows, tree);
        }
        break;
      case 'ArrowLeft':
        event.preventDefault();
        if (!node.isFile && this.expanded().has(node.path)) {
          this.setExpanded(node.path, false);
        } else {
          const parent = parentIndex(rows, index);
          if (parent >= 0) this.focusRowAt(parent, rows, tree);
        }
        break;
      case 'Enter':
      case ' ':
        event.preventDefault();
        this.onClick(node);
        break;
      case 'Home':
        event.preventDefault();
        this.focusRowAt(0, rows, tree);
        break;
      case 'End':
        event.preventDefault();
        this.focusRowAt(rows.length - 1, rows, tree);
        break;
    }
  }

  private focusRowAt(index: number, rows: TreeNode[], tree: HTMLElement): void {
    const node = rows[index];
    if (!node) return;
    this.activePath.set(node.path);
    // Moving onto a file selects it so the diff loads on the right; folders
    // only take focus (expand / collapse stays an explicit ←/→/Enter action).
    if (node.isFile && this.selected() !== node.path) {
      this.selectRequest.emit(node.path);
    }
    tree.querySelectorAll<HTMLElement>('li.git-tree__row')[index]?.focus();
  }

  private setExpanded(path: string, on: boolean): void {
    const files = this.files();
    const next = new Set(this.expanded());
    if (on) next.add(path); else next.delete(path);
    this.userExpanded.set({ files, set: next });
  }
}

/** Basename of a repo-relative path (segment after the last forward/back slash). */
function baseName(path: string): string {
  if (!path) return '';
  const normalized = path.replace(/\\/g, '/');
  const slash = normalized.lastIndexOf('/');
  return slash >= 0 ? normalized.slice(slash + 1) : normalized;
}

/** Index of the nearest preceding shallower row (the folder that contains `index`). */
function parentIndex(rows: readonly TreeNode[], index: number): number {
  const depth = rows[index].depth;
  for (let i = index - 1; i >= 0; i--) {
    if (rows[i].depth < depth) return i;
  }
  return -1;
}

function buildTree(files: readonly GitFileChange[]): TreeNode[] {
  // Build a nested directory tree from forward-slash paths. Status / numstat
  // ride on the leaf; folders sum +/- across their descendants.
  interface Bucket { name: string; isFile: boolean; file?: GitFileChange; children: Map<string, Bucket> }
  const root: Bucket = { name: '', isFile: false, children: new Map() };

  for (const f of files) {
    if (!f.path) continue;
    const parts = f.path.replace(/\\/g, '/').split('/').filter(p => p.length > 0);
    if (parts.length === 0) continue;
    let cursor = root;
    for (let i = 0; i < parts.length; i++) {
      const segment = parts[i];
      const isLeaf = i === parts.length - 1;
      let next = cursor.children.get(segment);
      if (!next) {
        next = { name: segment, isFile: isLeaf, children: new Map() };
        cursor.children.set(segment, next);
      }
      if (isLeaf) {
        next.isFile = true;
        next.file = f;
      }
      cursor = next;
    }
  }

  return convert(root, '', 0);

  function convert(bucket: Bucket, parentPath: string, depth: number): TreeNode[] {
    const nodes: TreeNode[] = [];
    // Folders before files at each level, both alphabetical (case-insensitive).
    const entries = [...bucket.children.values()].sort(sortBuckets);
    for (const child of entries) {
      const childPath = parentPath ? `${parentPath}/${child.name}` : child.name;
      if (child.isFile && child.children.size === 0) {
        const f = child.file!;
        nodes.push({
          path: childPath,
          label: child.name,
          isFile: true,
          status: f.status,
          added: f.added,
          removed: f.removed,
          count: 1,
          children: [],
          depth
        });
        continue;
      }
      // Fold single-child folder chains: while the only entry is another
      // pure folder, append its name to the label so "frontend/src/app"
      // collapses to one row.
      let folded = child;
      let foldedPath = childPath;
      let label = child.name;
      while (
        !folded.isFile
        && folded.children.size === 1
        && [...folded.children.values()][0].children.size > 0
        && !([...folded.children.values()][0].isFile)
      ) {
        const onlyChild = [...folded.children.values()][0];
        label = `${label}/${onlyChild.name}`;
        foldedPath = `${foldedPath}/${onlyChild.name}`;
        folded = onlyChild;
      }
      const children = convert(folded, foldedPath, depth + 1);
      const agg = aggregate(children);
      nodes.push({
        path: foldedPath,
        label,
        isFile: false,
        status: '',
        added: agg.added,
        removed: agg.removed,
        count: agg.files,
        children,
        depth
      });
    }
    return nodes;
  }

  function sortBuckets(a: Bucket, b: Bucket): number {
    const aFolder = !a.isFile || a.children.size > 0;
    const bFolder = !b.isFile || b.children.size > 0;
    if (aFolder !== bFolder) return aFolder ? -1 : 1;
    return a.name.localeCompare(b.name, undefined, { sensitivity: 'base' });
  }
}

function aggregate(nodes: TreeNode[]): { files: number; added: number; removed: number } {
  let files = 0, added = 0, removed = 0;
  for (const n of nodes) {
    if (n.isFile) { files += 1; added += n.added; removed += n.removed; }
    else { files += n.count; added += n.added; removed += n.removed; }
  }
  return { files, added, removed };
}

function countFiles(nodes: TreeNode[]): number {
  let n = 0;
  for (const node of nodes) {
    if (node.isFile) n += 1; else n += countFiles(node.children);
  }
  return n;
}

function collectFolderPaths(nodes: TreeNode[], into: Set<string>): void {
  for (const node of nodes) {
    if (!node.isFile) {
      into.add(node.path);
      collectFolderPaths(node.children, into);
    }
  }
}

function walkVisible(nodes: TreeNode[], expanded: ReadonlySet<string>, out: TreeNode[]): void {
  for (const node of nodes) {
    out.push(node);
    if (!node.isFile && expanded.has(node.path)) {
      walkVisible(node.children, expanded, out);
    }
  }
}
