import type { RunFileChange } from '../../../../../features/run-timeline';

export interface TreeNode {
  name: string;
  fullPath: string;
  isFolder: boolean;
  added: number;
  removed: number;
  fileCount: number;
  children: TreeNode[];
  change?: RunFileChange;
}

export interface DiffLine {
  text: string;
  prefix: string;
  body: string;
  kind: 'add' | 'del' | 'hunk' | 'meta' | 'ctx';
}

export interface HighlightedLine extends DiffLine {
  highlightedHtml: string | null;
}

export function buildTree(files: RunFileChange[]): TreeNode[] {
  const root: TreeNode = {
    name: '',
    fullPath: '',
    isFolder: true,
    added: 0,
    removed: 0,
    fileCount: 0,
    children: [],
  };
  const folderIndex = new Map<string, TreeNode>();
  folderIndex.set('', root);

  for (const f of files) {
    const segments = f.path.split('/');
    let parent = root;
    let prefix = '';
    for (let i = 0; i < segments.length; i++) {
      const seg = segments[i];
      const isLast = i === segments.length - 1;
      const path = prefix ? `${prefix}/${seg}` : seg;
      if (isLast) {
        parent.children.push({
          name: seg,
          fullPath: path,
          isFolder: false,
          added: f.added,
          removed: f.removed,
          fileCount: 1,
          children: [],
          change: f,
        });
      } else {
        let folder = folderIndex.get(path);
        if (!folder) {
          folder = {
            name: seg,
            fullPath: path,
            isFolder: true,
            added: 0,
            removed: 0,
            fileCount: 0,
            children: [],
          };
          folderIndex.set(path, folder);
          parent.children.push(folder);
        }
        parent = folder;
      }
      prefix = path;
    }
  }

  function aggregate(node: TreeNode): void {
    if (!node.isFolder) return;
    let added = 0,
      removed = 0,
      fileCount = 0;
    for (const c of node.children) {
      aggregate(c);
      added += c.added;
      removed += c.removed;
      fileCount += c.fileCount;
    }
    node.added = added;
    node.removed = removed;
    node.fileCount = fileCount;
    node.children.sort((a, b) => {
      if (a.isFolder !== b.isFolder) return a.isFolder ? -1 : 1;
      return a.name.localeCompare(b.name);
    });
  }
  aggregate(root);
  return root.children;
}

export function findFirstLeaf(nodes: TreeNode[]): TreeNode | null {
  for (const n of nodes) {
    if (!n.isFolder) return n;
    const inner = findFirstLeaf(n.children);
    if (inner) return inner;
  }
  return null;
}

export function splitDiff(raw: string): DiffLine[] {
  if (!raw) return [];
  const lines = raw.replace(/\r\n/g, '\n').split('\n');
  const out: DiffLine[] = [];
  for (const line of lines) {
    if (line.startsWith('@@')) {
      out.push({ text: line, prefix: '', body: line, kind: 'hunk' });
    } else if (
      line.startsWith('+++') ||
      line.startsWith('---') ||
      line.startsWith('diff ') ||
      line.startsWith('index ') ||
      line.startsWith('similarity ') ||
      line.startsWith('rename ') ||
      line.startsWith('new file ') ||
      line.startsWith('deleted file ')
    ) {
      out.push({ text: line, prefix: '', body: line, kind: 'meta' });
    } else if (line.startsWith('+')) {
      out.push({ text: line, prefix: '+', body: line.slice(1), kind: 'add' });
    } else if (line.startsWith('-')) {
      out.push({ text: line, prefix: '-', body: line.slice(1), kind: 'del' });
    } else if (line.startsWith(' ')) {
      out.push({ text: line, prefix: ' ', body: line.slice(1), kind: 'ctx' });
    } else {
      out.push({ text: line, prefix: '', body: line, kind: 'ctx' });
    }
  }
  return out;
}

export function detectLanguage(path: string | null): string | null {
  if (!path) return null;
  const lower = path.toLowerCase();
  const dot = lower.lastIndexOf('.');
  if (dot < 0) return null;
  const ext = lower.slice(dot + 1);
  switch (ext) {
    case 'ts':
    case 'tsx':
    case 'mts':
    case 'cts':
      return 'typescript';
    case 'js':
    case 'jsx':
    case 'mjs':
    case 'cjs':
      return 'javascript';
    case 'json':
    case 'jsonc':
      return 'json';
    case 'sh':
    case 'bash':
    case 'zsh':
      return 'bash';
    case 'cs':
      return 'csharp';
    case 'html':
    case 'htm':
    case 'xml':
    case 'svg':
      return 'html';
    case 'scss':
    case 'sass':
    case 'css':
      return 'scss';
    case 'md':
    case 'markdown':
      return 'markdown';
    case 'py':
      return 'python';
    case 'yml':
    case 'yaml':
      return 'yaml';
    default:
      return null;
  }
}

export function detectNewFile(raw: string): boolean {
  if (!raw) return false;
  const head = raw.slice(0, 512);
  if (/^new file mode /m.test(head)) return true;
  if (/^--- \/dev\/null/m.test(head)) return true;
  return false;
}
