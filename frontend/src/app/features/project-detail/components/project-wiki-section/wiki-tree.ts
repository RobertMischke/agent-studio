import { WikiFileEntry, WikiOrganization } from '../../../../models/project-docs.model';

/** Synthetic group id that collects every file not pinned by the manifest. */
export const UNGROUPED_ID = '__ungrouped__';

/**
 * One node in the rendered wiki tree. A `group` is a container (either a
 * user-defined theme from the manifest, or the synthetic "Ungrouped" bucket);
 * a `doc` points at a concrete wiki file. `synthetic` marks the Ungrouped
 * bucket — it cannot be renamed, moved, or deleted.
 */
export interface WikiTreeNode {
  id: string;
  kind: 'group' | 'doc';
  title: string;
  /** Set on doc nodes; the relPath of the underlying wiki file. */
  relPath: string | null;
  /** True only for the synthetic Ungrouped bucket. */
  synthetic: boolean;
  children: WikiTreeNode[];
}

/** A flattened, depth-tagged row ready for `@for` rendering. */
export interface WikiTreeRow {
  node: WikiTreeNode;
  depth: number;
  hasChildren: boolean;
  expanded: boolean;
}

/**
 * Builds the display tree from the immutable file list plus the user's
 * organisation manifest. Manifest `group` nodes form the hierarchy (nesting via
 * parentId); manifest `doc` nodes pin files into groups with an optional title
 * override. Any file the manifest does not place falls into a synthetic
 * "Ungrouped" group so nothing is ever hidden. Stale doc-nodes (relPath no
 * longer on disk) and nodes with broken parent chains degrade gracefully.
 */
export function buildWikiTree(
  files: readonly WikiFileEntry[],
  org: WikiOrganization | null,
): WikiTreeNode[] {
  const filesByRel = new Map<string, WikiFileEntry>();
  for (const f of files) filesByRel.set(f.relPath, f);

  const nodes = org?.nodes ?? [];

  // Group nodes first so doc/sub-group parents always resolve.
  const groupById = new Map<string, WikiTreeNode>();
  const orderById = new Map<string, number>();
  const parentById = new Map<string, string | null>();
  for (const n of nodes) {
    if (n.type !== 'group' || !n.id) continue;
    if (groupById.has(n.id)) continue;
    groupById.set(n.id, {
      id: n.id,
      kind: 'group',
      title: (n.title ?? '').trim() || 'Untitled group',
      relPath: null,
      synthetic: false,
      children: [],
    });
    orderById.set(n.id, n.order);
    parentById.set(n.id, n.parentId);
  }

  const roots: WikiTreeNode[] = [];
  const placed = new Set<string>();

  // Attach groups to their parent (or root), guarding against cycles / missing
  // parents by walking the chain and falling back to root.
  for (const g of groupById.values()) {
    const parentId = resolveParent(g.id, parentById, groupById);
    if (parentId) groupById.get(parentId)!.children.push(g);
    else roots.push(g);
  }

  // Pin docs from the manifest into their group (or root).
  for (const n of nodes) {
    if (n.type !== 'doc' || !n.relPath) continue;
    const file = filesByRel.get(n.relPath);
    if (!file || placed.has(n.relPath)) continue;
    placed.add(n.relPath);
    const node: WikiTreeNode = {
      id: docId(n.relPath),
      kind: 'doc',
      title: (n.title ?? '').trim() || file.title || file.name,
      relPath: n.relPath,
      synthetic: false,
      children: [],
    };
    orderById.set(node.id, n.order);
    const parent = n.parentId ? groupById.get(n.parentId) : null;
    if (parent) parent.children.push(node);
    else roots.push(node);
  }

  // Everything the manifest did not place → synthetic Ungrouped bucket.
  const ungrouped = files
    .filter(f => !placed.has(f.relPath))
    .sort((a, b) => a.relPath.localeCompare(b.relPath))
    .map<WikiTreeNode>(f => ({
      id: docId(f.relPath),
      kind: 'doc',
      title: f.title || f.name,
      relPath: f.relPath,
      synthetic: false,
      children: [],
    }));
  if (ungrouped.length > 0) {
    roots.push({
      id: UNGROUPED_ID,
      kind: 'group',
      title: 'Ungrouped',
      relPath: null,
      synthetic: true,
      children: ungrouped,
    });
  }

  sortChildren(roots, orderById);
  return roots;
}

/** Stable node id for a doc, namespaced so it never collides with a group id. */
export function docId(relPath: string): string {
  return `doc:${relPath}`;
}

function resolveParent(
  startId: string,
  parentById: Map<string, string | null>,
  groupById: Map<string, WikiTreeNode>,
): string | null {
  const parentId = parentById.get(startId) ?? null;
  if (!parentId || !groupById.has(parentId)) return null;
  // Walk up to detect a cycle; if we loop back to startId, drop to root.
  const seen = new Set<string>([startId]);
  let cur: string | null = parentId;
  while (cur) {
    if (seen.has(cur)) return null;
    seen.add(cur);
    cur = parentById.get(cur) ?? null;
    if (cur && !groupById.has(cur)) return null;
  }
  return parentId;
}

function sortChildren(nodes: WikiTreeNode[], orderById: Map<string, number>): void {
  nodes.sort((a, b) => compareNodes(a, b, orderById));
  for (const n of nodes) {
    if (n.children.length > 0) sortChildren(n.children, orderById);
  }
}

function compareNodes(
  a: WikiTreeNode,
  b: WikiTreeNode,
  orderById: Map<string, number>,
): number {
  // The synthetic Ungrouped bucket always sinks to the bottom.
  if (a.synthetic !== b.synthetic) return a.synthetic ? 1 : -1;
  const oa = orderById.get(a.id);
  const ob = orderById.get(b.id);
  if (oa !== undefined && ob !== undefined && oa !== ob) return oa - ob;
  if (oa !== undefined && ob === undefined) return -1;
  if (oa === undefined && ob !== undefined) return 1;
  return a.title.localeCompare(b.title);
}

/**
 * Flattens the tree to a render list, descending into a group only when its id
 * is in `expanded`. Always lists groups before their (expanded) children.
 */
export function flattenWikiTree(
  roots: readonly WikiTreeNode[],
  expanded: ReadonlySet<string>,
): WikiTreeRow[] {
  const out: WikiTreeRow[] = [];
  const walk = (nodes: readonly WikiTreeNode[], depth: number): void => {
    for (const node of nodes) {
      const hasChildren = node.children.length > 0;
      const isOpen = node.kind === 'group' && expanded.has(node.id);
      out.push({ node, depth, hasChildren, expanded: isOpen });
      if (isOpen) walk(node.children, depth + 1);
    }
  };
  walk(roots, 0);
  return out;
}

/**
 * Drops group nodes that contain no doc descendants. Used when a text filter is
 * active so empty themes don't clutter the result set; docs are always kept.
 */
export function pruneEmptyGroups(roots: readonly WikiTreeNode[]): WikiTreeNode[] {
  const keep = (n: WikiTreeNode): WikiTreeNode | null => {
    if (n.kind === 'doc') return n;
    const children = n.children
      .map(keep)
      .filter((c): c is WikiTreeNode => c !== null);
    return children.length > 0 ? { ...n, children } : null;
  };
  return roots.map(keep).filter((c): c is WikiTreeNode => c !== null);
}

/** Collects the ids of every group node in the tree (for "expand all" seeding). */
export function collectGroupIds(roots: readonly WikiTreeNode[]): string[] {
  const out: string[] = [];
  const walk = (nodes: readonly WikiTreeNode[]): void => {
    for (const n of nodes) {
      if (n.kind === 'group') out.push(n.id);
      if (n.children.length > 0) walk(n.children);
    }
  };
  walk(roots);
  return out;
}
