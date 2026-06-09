import { describe, expect, it } from 'vitest';
import { WikiFileEntry, WikiOrganization, WikiOrgNode } from '../../../../models/project-docs.model';
import {
  UNGROUPED_ID,
  WikiTreeNode,
  buildWikiTree,
  collectGroupIds,
  docId,
  flattenWikiTree,
  pruneEmptyGroups,
} from './wiki-tree';

const file = (relPath: string, title = relPath): WikiFileEntry => ({
  name: relPath.split('/').pop()!,
  relPath,
  title,
  updatedAt: '2026-06-09T00:00:00Z',
  size: 1,
});

const group = (id: string, title: string, parentId: string | null = null, order = 0): WikiOrgNode =>
  ({ id, type: 'group', title, relPath: null, parentId, order });

const doc = (relPath: string, parentId: string | null, order = 0, title: string | null = null): WikiOrgNode =>
  ({ id: docId(relPath), type: 'doc', title, relPath, parentId, order });

const org = (nodes: WikiOrgNode[]): WikiOrganization => ({ version: 1, nodes });

const childTitles = (n: WikiTreeNode) => n.children.map(c => c.title);

describe('buildWikiTree', () => {
  it('places every file under Ungrouped when there is no manifest', () => {
    const roots = buildWikiTree([file('b.md'), file('a.md')], null);

    expect(roots).toHaveLength(1);
    expect(roots[0].id).toBe(UNGROUPED_ID);
    expect(roots[0].synthetic).toBe(true);
    // sorted by relPath
    expect(roots[0].children.map(c => c.relPath)).toEqual(['a.md', 'b.md']);
  });

  it('pins docs into a user group and keeps the rest under Ungrouped', () => {
    const roots = buildWikiTree(
      [file('guide.md'), file('loose.md')],
      org([group('g1', 'Themes'), doc('guide.md', 'g1')]),
    );

    expect(roots.map(r => r.id)).toEqual(['g1', UNGROUPED_ID]);
    const g1 = roots.find(r => r.id === 'g1')!;
    expect(g1.children.map(c => c.relPath)).toEqual(['guide.md']);
    const ungrouped = roots.find(r => r.id === UNGROUPED_ID)!;
    expect(ungrouped.children.map(c => c.relPath)).toEqual(['loose.md']);
  });

  it('nests sub-groups under their parent', () => {
    const roots = buildWikiTree(
      [file('x.md')],
      org([
        group('parent', 'Parent'),
        group('child', 'Child', 'parent'),
        doc('x.md', 'child'),
      ]),
    );

    const parent = roots.find(r => r.id === 'parent')!;
    expect(parent.children.map(c => c.id)).toEqual(['child']);
    expect(parent.children[0].children.map(c => c.relPath)).toEqual(['x.md']);
  });

  it('applies a doc title override from the manifest', () => {
    const roots = buildWikiTree(
      [file('guide.md', 'Original')],
      org([group('g1', 'G'), doc('guide.md', 'g1', 0, 'Renamed')]),
    );
    const g1 = roots.find(r => r.id === 'g1')!;
    expect(childTitles(g1)).toEqual(['Renamed']);
  });

  it('drops stale doc-nodes whose file no longer exists', () => {
    const roots = buildWikiTree(
      [file('still-here.md')],
      org([group('g1', 'G'), doc('gone.md', 'g1'), doc('still-here.md', 'g1')]),
    );
    const g1 = roots.find(r => r.id === 'g1')!;
    expect(g1.children.map(c => c.relPath)).toEqual(['still-here.md']);
  });

  it('falls a doc back to root when its parent group is missing', () => {
    const roots = buildWikiTree(
      [file('orphan.md')],
      org([doc('orphan.md', 'no-such-group')]),
    );
    // No real group, so the doc sits at root and nothing is Ungrouped.
    expect(roots.map(r => r.id)).toEqual([docId('orphan.md')]);
  });

  it('sorts groups by order then title and sinks Ungrouped to the bottom', () => {
    const roots = buildWikiTree(
      [file('a.md'), file('loose.md')],
      org([
        group('second', 'Second', null, 1),
        group('first', 'First', null, 0),
        doc('a.md', 'first'),
      ]),
    );
    expect(roots.map(r => r.id)).toEqual(['first', 'second', UNGROUPED_ID]);
  });

  it('breaks a parent cycle by dropping the looped group to root', () => {
    const roots = buildWikiTree(
      [],
      org([group('a', 'A', 'b'), group('b', 'B', 'a')]),
    );
    // Both reference each other; neither can nest, so both land at root.
    expect(roots.map(r => r.id).sort()).toEqual(['a', 'b']);
  });
});

describe('flattenWikiTree', () => {
  it('lists groups collapsed and only descends into expanded ones', () => {
    const roots = buildWikiTree(
      [file('a.md')],
      org([group('g1', 'G1'), doc('a.md', 'g1')]),
    );

    const collapsed = flattenWikiTree(roots, new Set());
    expect(collapsed.map(r => r.node.id)).toEqual(['g1']);
    expect(collapsed[0].hasChildren).toBe(true);
    expect(collapsed[0].expanded).toBe(false);

    const expanded = flattenWikiTree(roots, new Set(['g1']));
    expect(expanded.map(r => r.node.id)).toEqual(['g1', docId('a.md')]);
    expect(expanded[1].depth).toBe(1);
  });
});

describe('pruneEmptyGroups', () => {
  it('removes groups with no doc descendants but keeps populated ones', () => {
    const roots = buildWikiTree(
      [file('a.md')],
      org([group('full', 'Full'), group('empty', 'Empty'), doc('a.md', 'full')]),
    );
    const pruned = pruneEmptyGroups(roots);
    expect(pruned.map(r => r.id)).toEqual(['full']);
  });

  it('keeps a parent whose only content is a populated sub-group', () => {
    const roots = buildWikiTree(
      [file('a.md')],
      org([group('p', 'P'), group('c', 'C', 'p'), doc('a.md', 'c')]),
    );
    const pruned = pruneEmptyGroups(roots);
    expect(pruned.map(r => r.id)).toEqual(['p']);
    expect(pruned[0].children.map(c => c.id)).toEqual(['c']);
  });
});

describe('collectGroupIds', () => {
  it('returns every group id including nested and synthetic', () => {
    const roots = buildWikiTree(
      [file('a.md'), file('loose.md')],
      org([group('p', 'P'), group('c', 'C', 'p'), doc('a.md', 'c')]),
    );
    expect(collectGroupIds(roots).sort()).toEqual(['c', 'p', UNGROUPED_ID].sort());
  });
});
