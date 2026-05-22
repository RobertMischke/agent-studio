import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal, DoCheck } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import type { JobInfo } from '../../../../../models/job.model';
import type { RunFileChange, RunRecord } from '../../../../../features/run-timeline';
import { JobService } from '../../../../../services/job.service';
import { highlightBlock } from '../../beautiful-results/highlight-lazy';

/**
 * One node in the file-tree the viewer renders on the left. Folders
 * are nested via {@link children}; leaves carry a {@link change} entry
 * and have no children. The tree is built from the flat
 * {@link RunFileChange}[] returned by `/api/jobs/.../runs/{n}/files`.
 *
 * Folder nodes aggregate their subtree's +/- counts so the user can
 * see the size of a change without expanding every directory.
 */
interface TreeNode {
  name: string;
  fullPath: string; // empty string for root
  isFolder: boolean;
  added: number;
  removed: number;
  fileCount: number; // number of file leaves in this subtree (1 for a leaf)
  children: TreeNode[];
  change?: RunFileChange; // populated for leaves
}

/**
 * The Big Git Viewer for one run. Rendered as a full-screen overlay
 * over the protocol pane (z-index above the chat strip, below any
 * top-level dialogs). The user opens it from a run card; the viewer
 * loads the run's aggregated file list, then a diff per selected
 * file. Both are derived from the deterministic SHA range
 * (HeadShaBefore..HeadShaAfter), so the same files+diffs are
 * available "in der Zukunft" as long as the commits exist on disk.
 *
 * Three columns:
 *
 * - **Header**: run id, captured SHAs, total +/- across all files,
 *   and a Close button.
 * - **Left**: a real folder tree, not a flat list. Folders show
 *   aggregate stats; clicking a folder collapses/expands it. Files
 *   show individual stats and the status letter (A/M/D/R/C).
 * - **Right**: the unified diff for the selected file, with a
 *   minimal coloured renderer (+/- lines, hunk headers) plus per-line
 *   syntax highlighting (lazy-loaded highlight.js, language picked
 *   from the file extension; falls back to plain monospace).
 *
 * The diff renderer is intentionally simple — we want a working
 * "show me what changed" view without pulling in a full diff
 * library. If we need word-level highlighting later, this is the
 * single place to upgrade.
 */
@Component({
  selector: 'app-run-git-viewer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet],
  templateUrl: './run-git-viewer.component.html',
  styleUrl: './run-git-viewer.component.scss'
})
export class RunGitViewerComponent implements DoCheck {
  readonly job = input<JobInfo | null>(null);
  readonly run = input<RunRecord | null>(null);
  readonly visible = input<boolean>(false);

  readonly closeRequest = output<void>();

  readonly files = signal<RunFileChange[]>([]);
  readonly filesState = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly filesError = signal<string | null>(null);

  readonly selectedPath = signal<string | null>(null);
  readonly diffText = signal<string>('');
  readonly diffState = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly diffError = signal<string | null>(null);

  /** Tracks which folders are open. Root is always considered open. */
  readonly expanded = signal<Set<string>>(new Set([''])); // root expanded
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');

  readonly totalAdded = computed(() => this.files().reduce((s, f) => s + f.added, 0));
  readonly totalRemoved = computed(() => this.files().reduce((s, f) => s + f.removed, 0));
  readonly totalFiles = computed(() => this.files().length);

  readonly rootChildren = computed<TreeNode[]>(() => buildTree(this.files()));

  readonly diffLines = computed<DiffLine[]>(() => splitDiff(this.diffText()));

  /**
   * Per-line syntax-highlighted HTML for the current diff. `null` while
   * highlighting is in flight or when no language was detected for the
   * selected file; in that case the template renders the raw body via
   * interpolation. The first hljs call lazy-loads the library, so the
   * highlighted view appears one microtask after the diff first renders.
   */
  readonly highlightedLines = signal<HighlightedLine[] | null>(null);

  /**
   * True when the diff payload indicates the file was added in this
   * range (`new file mode` or git's `diff --git ... /dev/null` source).
   * Used to drop the green add-overlay tint on every line, matching
   * VS Code's behaviour for net-new files.
   */
  readonly isNewFile = computed<boolean>(() => detectNewFile(this.diffText()));

  private readonly jobService = inject(JobService);
  private currentLoadKey = '';

  ngDoCheck(): void {
    // Cheap identity check: when the modal becomes visible with a new
    // (job, run) pair, kick off the file load. This avoids requiring
    // an explicit (open) event from the parent.
    const v = this.visible();
    const j = this.job();
    const r = this.run();
    const key = v && j && r ? `${j.id}::${r.index}` : '';
    if (key && key !== this.currentLoadKey) {
      this.currentLoadKey = key;
      this.loadFiles();
    } else if (!key) {
      this.currentLoadKey = '';
    }
  }

  copyLabel(): string {
    const s = this.copyState();
    if (s === 'copied') return '✓ Copied';
    if (s === 'failed') return '⚠ Copy failed';
    return '📋 Copy diff';
  }

  copyDiff(): void {
    const text = this.diffText();
    if (!text) return;
    navigator.clipboard?.writeText(text).then(
      () => {
        this.copyState.set('copied');
        setTimeout(() => this.copyState.set('idle'), 2000);
      },
      () => {
        this.copyState.set('failed');
        setTimeout(() => this.copyState.set('idle'), 2000);
      }
    );
  }

  short(sha: string | null): string {
    if (!sha) return '';
    return sha.length > 12 ? sha.slice(0, 8) : sha;
  }

  onNodeClick(node: TreeNode): void {
    if (node.isFolder) {
      const next = new Set(this.expanded());
      if (next.has(node.fullPath)) next.delete(node.fullPath);
      else next.add(node.fullPath);
      this.expanded.set(next);
      return;
    }
    if (!node.change) return;
    if (this.selectedPath() === node.fullPath) return;
    this.selectedPath.set(node.fullPath);
    this.loadDiff(node.fullPath);
  }

  private loadFiles(): void {
    const job = this.job();
    const run = this.run();
    if (!job || !run) return;

    this.filesState.set('loading');
    this.filesError.set(null);
    this.selectedPath.set(null);
    this.diffText.set('');
    this.diffState.set('idle');
    this.highlightedLines.set(null);

    this.jobService.getRunFiles(job.id, run.index, job.watchPath).subscribe({
      next: (res) => {
        this.files.set(res.files ?? []);
        // Pre-expand the first level of folders so the user sees content.
        const e = new Set<string>(['']);
        for (const f of res.files ?? []) {
          const segments = f.path.split('/');
          // Add every prefix folder so the path to the first file is open.
          for (let i = 1; i < segments.length; i++) {
            e.add(segments.slice(0, i).join('/'));
          }
          // Only pre-expand the first file's path - past that, let the
          // user drive expansion to keep large changes legible.
          break;
        }
        this.expanded.set(e);
        this.filesState.set('loaded');
        // Auto-select the first file so the diff pane is not empty.
        const firstLeaf = findFirstLeaf(this.rootChildren());
        if (firstLeaf) {
          this.selectedPath.set(firstLeaf.fullPath);
          this.loadDiff(firstLeaf.fullPath);
        }
      },
      error: (err) => {
        this.filesError.set(err?.error?.error || err?.message || 'Could not load files.');
        this.filesState.set('error');
      }
    });
  }

  private loadDiff(path: string): void {
    const job = this.job();
    const run = this.run();
    if (!job || !run) return;
    this.diffState.set('loading');
    this.diffError.set(null);
    this.diffText.set('');
    this.highlightedLines.set(null);
    this.jobService.getRunDiff(job.id, run.index, path, job.watchPath).subscribe({
      next: (res) => {
        this.diffText.set(res.diff ?? '');
        this.diffState.set('loaded');
        // Kick off async per-line highlighting. We don't await; the
        // diff renders un-highlighted first (one frame), then the
        // signal flips and the template re-renders with hljs spans.
        this.highlightCurrentDiff(path);
      },
      error: (err) => {
        this.diffError.set(err?.error?.error || err?.message || 'Could not load diff.');
        this.diffState.set('error');
      }
    });
  }

  /**
   * Runs highlight.js per code line (add/del/ctx) for the current
   * diff. We pass the captured `forPath` along so a fast file-switch
   * doesn't race a stale highlight back into the signal.
   */
  private async highlightCurrentDiff(forPath: string): Promise<void> {
    const language = detectLanguage(forPath);
    if (!language) {
      this.highlightedLines.set(null);
      return;
    }
    const lines = this.diffLines();
    const tasks = lines.map(async (line) => {
      if (line.kind === 'add' || line.kind === 'del' || line.kind === 'ctx') {
        const { html } = await highlightBlock(line.body, language);
        return { ...line, highlightedHtml: html };
      }
      return { ...line, highlightedHtml: null };
    });
    const highlighted = await Promise.all(tasks);
    if (this.selectedPath() === forPath) {
      this.highlightedLines.set(highlighted);
    }
  }
}

/**
 * Builds a folder tree from the flat file list. Folders aggregate
 * +/- counts and file counts so the user can scan the change size
 * without expanding everything.
 */
function buildTree(files: RunFileChange[]): TreeNode[] {
  const root: TreeNode = { name: '', fullPath: '', isFolder: true, added: 0, removed: 0, fileCount: 0, children: [] };
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
        const leaf: TreeNode = {
          name: seg,
          fullPath: path,
          isFolder: false,
          added: f.added,
          removed: f.removed,
          fileCount: 1,
          children: [],
          change: f
        };
        parent.children.push(leaf);
      } else {
        let folder = folderIndex.get(path);
        if (!folder) {
          folder = { name: seg, fullPath: path, isFolder: true, added: 0, removed: 0, fileCount: 0, children: [] };
          folderIndex.set(path, folder);
          parent.children.push(folder);
        }
        parent = folder;
      }
      prefix = path;
    }
  }

  // Aggregate counts up the tree. Sort folders before files at each level,
  // alphabetic within each group.
  function aggregate(node: TreeNode): void {
    if (!node.isFolder) return;
    let added = 0, removed = 0, fileCount = 0;
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

function findFirstLeaf(nodes: TreeNode[]): TreeNode | null {
  for (const n of nodes) {
    if (!n.isFolder) return n;
    const inner = findFirstLeaf(n.children);
    if (inner) return inner;
  }
  return null;
}

interface DiffLine {
  /** Raw line as read from the diff payload, including the leading +/-/space prefix. */
  text: string;
  /** Diff prefix character: '+', '-', ' ', or '' for hunk/meta. */
  prefix: string;
  /** Body of the line with the prefix stripped (for hljs input). */
  body: string;
  kind: 'add' | 'del' | 'hunk' | 'meta' | 'ctx';
}

interface HighlightedLine extends DiffLine {
  /** hljs HTML for {@link DiffLine.body}, or null when no language was detected. */
  highlightedHtml: string | null;
}

function splitDiff(raw: string): DiffLine[] {
  if (!raw) return [];
  const lines = raw.replace(/\r\n/g, '\n').split('\n');
  const out: DiffLine[] = [];
  for (const line of lines) {
    if (line.startsWith('@@')) {
      out.push({ text: line, prefix: '', body: line, kind: 'hunk' });
    } else if (
      line.startsWith('+++') || line.startsWith('---') ||
      line.startsWith('diff ') || line.startsWith('index ') ||
      line.startsWith('similarity ') || line.startsWith('rename ') ||
      line.startsWith('new file ') || line.startsWith('deleted file ')
    ) {
      out.push({ text: line, prefix: '', body: line, kind: 'meta' });
    } else if (line.startsWith('+')) {
      out.push({ text: line, prefix: '+', body: line.slice(1), kind: 'add' });
    } else if (line.startsWith('-')) {
      out.push({ text: line, prefix: '-', body: line.slice(1), kind: 'del' });
    } else if (line.startsWith(' ')) {
      out.push({ text: line, prefix: ' ', body: line.slice(1), kind: 'ctx' });
    } else {
      // Trailing empty line at EOF, or a stray prefix-less line: treat as
      // context with an empty prefix so the renderer doesn't shift columns.
      out.push({ text: line, prefix: '', body: line, kind: 'ctx' });
    }
  }
  return out;
}

/**
 * Maps a file path to a highlight.js language id. Returning null
 * means "render as plain monospace" — the highlightedLines signal
 * stays at null and the template falls back to text interpolation.
 */
function detectLanguage(path: string | null): string | null {
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

/**
 * Detects "this diff adds a brand-new file" so the renderer can drop
 * the green per-line tint. Checks both the `new file mode` line that
 * git emits and the `--- /dev/null` source marker.
 */
function detectNewFile(raw: string): boolean {
  if (!raw) return false;
  // Limit scan: only the first ~6 lines of a unified diff carry these markers.
  const head = raw.slice(0, 512);
  if (/^new file mode /m.test(head)) return true;
  if (/^--- \/dev\/null/m.test(head)) return true;
  return false;
}
