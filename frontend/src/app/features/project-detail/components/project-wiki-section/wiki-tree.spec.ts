import { describe, expect, it } from 'vitest';
import { WikiTreeNode } from '../../../../models/project-docs.model';
import {
  collectFolderIds,
  filterWikiTree,
  flattenWikiTree,
  nodeId,
} from './wiki-tree';

const file = (relPath: string, type: 'md' | 'html' = 'md', title = relPath): WikiTreeNode => ({
  name: relPath.split('/').pop()!,
  title,
  relPath,
  type,
  children: [],
});

const folder = (relPath: string, children: WikiTreeNode[], title = relPath): WikiTreeNode => ({
  name: relPath.split('/').pop()!,
  title,
  relPath,
  type: 'folder',
  children,
});

describe('flattenWikiTree', () => {
  it('lists folders collapsed and only descends into expanded ones', () => {
    const roots = [folder('concepts', [file('concepts/a.md', 'md', 'A')]), file('README.md', 'md', 'Index')];

    const collapsed = flattenWikiTree(roots, new Set());
    expect(collapsed.map(r => nodeId(r.node))).toEqual(['concepts', 'README.md']);
    const folderRow = collapsed[0];
    expect(folderRow.hasChildren).toBe(true);
    expect(folderRow.expanded).toBe(false);

    const expanded = flattenWikiTree(roots, new Set(['concepts']));
    expect(expanded.map(r => nodeId(r.node))).toEqual(['concepts', 'concepts/a.md', 'README.md']);
    expect(expanded[1].depth).toBe(1);
  });

  it('surfaces html doc nodes alongside markdown', () => {
    const roots = [folder('concepts', [file('concepts/page.html', 'html', 'Page')])];
    const rows = flattenWikiTree(roots, new Set(['concepts']));
    const leaf = rows.find(r => r.node.relPath === 'concepts/page.html')!;
    expect(leaf.node.type).toBe('html');
  });
});

describe('filterWikiTree', () => {
  it('returns the tree unchanged for an empty needle', () => {
    const roots = [file('a.md'), file('b.md')];
    expect(filterWikiTree(roots, '   ').map(n => n.relPath)).toEqual(['a.md', 'b.md']);
  });

  it('keeps matching files and the folders that lead to them', () => {
    const roots = [
      folder('concepts', [file('concepts/overview.md', 'md', 'Concept overview'), file('concepts/misc.md', 'md', 'Misc')]),
      file('README.md', 'md', 'Docs index'),
    ];
    const filtered = filterWikiTree(roots, 'concept');
    // README drops out; the concepts folder is kept but only the matching child.
    expect(filtered.map(n => n.relPath)).toEqual(['concepts']);
    expect(filtered[0].children.map(c => c.relPath)).toEqual(['concepts/overview.md']);
  });

  it('keeps a folder when the folder name itself matches', () => {
    const roots = [folder('architecture', [file('architecture/x.md', 'md', 'X')])];
    const filtered = filterWikiTree(roots, 'architect');
    expect(filtered.map(n => n.relPath)).toEqual(['architecture']);
  });
});

describe('collectFolderIds', () => {
  it('returns every folder id including nested ones', () => {
    const roots = [
      folder('a', [folder('a/b', [file('a/b/c.md')])]),
      file('top.md'),
    ];
    expect(collectFolderIds(roots).sort()).toEqual(['a', 'a/b'].sort());
  });
});
