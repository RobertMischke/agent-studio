import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import type { GitFileChange } from '../../../../features/git';

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
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './git-file-tree.component.html',
  styleUrl: './git-file-tree.component.scss'
})
export class GitFileTreeComponent {
  readonly files = input.required<readonly GitFileChange[]>();
  readonly selected = input<string | null>(null);
  /** Stretch the tree to fill the parent height (used in pane-maximized split layout). */
  readonly fill = input<boolean>(false);
  readonly select = output<string>();

  readonly tree = computed<TreeNode[]>(() => buildTree(this.files()));

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

  onClick(node: TreeNode): void {
    if (node.isFile) {
      this.select.emit(node.path);
      return;
    }
    const files = this.files();
    const next = new Set(this.expanded());
    if (next.has(node.path)) next.delete(node.path); else next.add(node.path);
    this.userExpanded.set({ files, set: next });
  }
}

function buildTree(files: readonly GitFileChange[]): TreeNode[] {
  // Build a nested directory tree from forward-slash paths. Status / numstat
  // ride on the leaf; folders sum +/- across their descendants.
  type Bucket = { name: string; isFile: boolean; file?: GitFileChange; children: Map<string, Bucket> };
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
