import { WikiTreeNode } from '../../../../models/project-docs.model';

/**
 * A flattened, depth-tagged row of the physical wiki tree, ready for `@for`
 * rendering. Folders are descended into only when expanded.
 */
export interface WikiTreeRow {
  node: WikiTreeNode;
  depth: number;
  hasChildren: boolean;
  expanded: boolean;
}

/**
 * Stable id for a tree node. The backend already guarantees a unique relPath per
 * node; folders and files never share one, so relPath doubles as the id. The
 * (never-rendered) synthetic root would have a null relPath, hence the fallback.
 */
export function nodeId(node: WikiTreeNode): string {
  return node.relPath ?? '';
}

/**
 * Flattens the physical tree to a render list, descending into a folder only
 * when its id is in `expanded`. Folders are always listed before their
 * (expanded) children, matching the backend's folders-first ordering.
 */
export function flattenWikiTree(
  roots: readonly WikiTreeNode[],
  expanded: ReadonlySet<string>,
): WikiTreeRow[] {
  const out: WikiTreeRow[] = [];
  const walk = (nodes: readonly WikiTreeNode[], depth: number): void => {
    for (const node of nodes) {
      const isFolder = node.type === 'folder';
      const hasChildren = node.children.length > 0;
      const isOpen = isFolder && expanded.has(nodeId(node));
      out.push({ node, depth, hasChildren, expanded: isOpen });
      if (isOpen) walk(node.children, depth + 1);
    }
  };
  walk(roots, 0);
  return out;
}

/**
 * Filters the tree to nodes matching `needle` (case-insensitive, against the
 * node's own title and basename). A folder is kept when it has a kept
 * descendant, so the path to every match stays navigable. Matching deliberately
 * ignores the ancestor-inclusive relPath: otherwise every file under a matched
 * folder would match via its folder prefix. An empty needle returns the tree
 * unchanged.
 */
export function filterWikiTree(
  roots: readonly WikiTreeNode[],
  needle: string,
): WikiTreeNode[] {
  const q = needle.trim().toLowerCase();
  if (!q) return [...roots];

  const matches = (n: WikiTreeNode): boolean =>
    n.title.toLowerCase().includes(q) || n.name.toLowerCase().includes(q);

  const keep = (n: WikiTreeNode): WikiTreeNode | null => {
    if (n.type !== 'folder') return matches(n) ? n : null;
    const children = n.children
      .map(keep)
      .filter((c): c is WikiTreeNode => c !== null);
    if (children.length > 0) return { ...n, children };
    return matches(n) ? { ...n, children: [] } : null;
  };

  return roots.map(keep).filter((c): c is WikiTreeNode => c !== null);
}

/** Collects the ids of every folder node in the tree (for "expand all" seeding). */
export function collectFolderIds(roots: readonly WikiTreeNode[]): string[] {
  const out: string[] = [];
  const walk = (nodes: readonly WikiTreeNode[]): void => {
    for (const n of nodes) {
      if (n.type === 'folder') out.push(nodeId(n));
      if (n.children.length > 0) walk(n.children);
    }
  };
  walk(roots);
  return out;
}
