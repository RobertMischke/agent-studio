import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import type { JobInfo } from '../../../../models/job.model';
import type { RunFileChange, RunRecord } from '../../../../features/run-timeline';
import { JobService } from '../../../../services/job.service';

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
 *   minimal coloured renderer (+/- lines, hunk headers).
 *
 * The diff renderer is intentionally simple - we want a working
 * "show me what changed" view without pulling in a full diff
 * library. If we need word-level highlighting later, this is the
 * single place to upgrade.
 */
@Component({
  selector: 'app-run-git-viewer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet],
  template: `
    @if (visible()) {
      <div class="rgv__backdrop" (click)="close.emit()"></div>
      <div class="rgv" role="dialog" aria-label="Run git viewer"
           data-testid="run-git-viewer"
           (click)="$event.stopPropagation()">
        <header class="rgv__header">
          <div class="rgv__title">
            <span class="rgv__title-main">Run #{{ run()?.index }} · git changes</span>
            <span class="rgv__sha-range">
              @if (run()?.headShaBefore && run()?.headShaAfter) {
                <code>{{ short(run()!.headShaBefore) }}</code>
                <span class="rgv__arrow">→</span>
                <code>{{ short(run()!.headShaAfter) }}</code>
              } @else {
                <em>no captured SHAs</em>
              }
            </span>
          </div>
          <div class="rgv__totals">
            @if (totalFiles() > 0) {
              <span class="rgv__total">{{ totalFiles() }} files</span>
              <span class="rgv__add">+{{ totalAdded() }}</span>
              <span class="rgv__del">-{{ totalRemoved() }}</span>
            }
          </div>
          <button class="rgv__close" type="button" (click)="close.emit()" aria-label="Close">✕</button>
        </header>

        <div class="rgv__body">
          <aside class="rgv__tree" data-testid="rgv-tree">
            @if (filesState() === 'loading') {
              <div class="rgv__placeholder">Loading file list…</div>
            } @else if (filesState() === 'error') {
              <div class="rgv__placeholder rgv__placeholder--error">{{ filesError() }}</div>
            } @else if (rootChildren().length === 0) {
              <div class="rgv__placeholder">No files changed in this run.</div>
            } @else {
              <ng-container *ngTemplateOutlet="treeTpl; context: { $implicit: rootChildren(), depth: 0 }" />
            }

            <ng-template #treeTpl let-nodes let-depth="depth">
              @for (n of nodes; track n.fullPath) {
                <div class="rgv__node"
                     [class.rgv__node--folder]="n.isFolder"
                     [class.rgv__node--file]="!n.isFolder"
                     [class.rgv__node--selected]="!n.isFolder && selectedPath() === n.fullPath"
                     [style.padding-left.px]="8 + depth * 14"
                     (click)="onNodeClick(n)">
                  <span class="rgv__node-glyph">
                    @if (n.isFolder) {
                      {{ expanded().has(n.fullPath) ? '▾' : '▸' }}
                    } @else if (n.change) {
                      <span class="rgv__status" [attr.data-status]="n.change.status">{{ n.change.status }}</span>
                    }
                  </span>
                  <span class="rgv__node-name">{{ n.name }}</span>
                  <span class="rgv__node-stats">
                    @if (n.isFolder) {
                      <span class="rgv__node-files">{{ n.fileCount }}</span>
                    }
                    @if (n.added > 0) { <span class="rgv__add">+{{ n.added }}</span> }
                    @if (n.removed > 0) { <span class="rgv__del">-{{ n.removed }}</span> }
                  </span>
                </div>
                @if (n.isFolder && expanded().has(n.fullPath)) {
                  <ng-container *ngTemplateOutlet="treeTpl; context: { $implicit: n.children, depth: depth + 1 }" />
                }
              }
            </ng-template>
          </aside>

          <section class="rgv__diff" data-testid="rgv-diff">
            @if (!selectedPath()) {
              <div class="rgv__placeholder">Select a file from the tree to view its diff.</div>
            } @else if (diffState() === 'loading') {
              <div class="rgv__placeholder">Loading diff for <code>{{ selectedPath() }}</code>…</div>
            } @else if (diffState() === 'error') {
              <div class="rgv__placeholder rgv__placeholder--error">{{ diffError() }}</div>
            } @else if (!diffText()) {
              <div class="rgv__placeholder">No diff available for <code>{{ selectedPath() }}</code> in this range.</div>
            } @else {
              <div class="rgv__diff-head">
                <code class="rgv__diff-path">{{ selectedPath() }}</code>
                <button class="rgv__copy" type="button" (click)="copyDiff()">{{ copyLabel() }}</button>
              </div>
              <pre class="rgv__diff-body" data-testid="rgv-diff-body"><code>@for (line of diffLines(); track $index) {<span [class]="'rgv__line rgv__line--' + line.kind">{{ line.text }}
</span>}</code></pre>
            }
          </section>
        </div>
      </div>
    }
  `,
  styles: [`
    :host { contain: layout; }
    .rgv__backdrop { position: fixed; inset: 0; background: rgba(2, 6, 23, 0.65); z-index: 50; }
    .rgv { position: fixed; inset: 24px; background: #0f172a; border: 1px solid rgba(148, 163, 184, 0.30); border-radius: 12px; z-index: 51; display: flex; flex-direction: column; box-shadow: 0 24px 64px rgba(0, 0, 0, 0.55); color: #e2e8f0; overflow: hidden; }

    .rgv__header { display: flex; align-items: center; gap: 16px; padding: 10px 14px; border-bottom: 1px solid rgba(148, 163, 184, 0.18); background: rgba(15, 23, 42, 0.85); }
    .rgv__title { flex: 1 1 auto; min-width: 0; display: flex; align-items: baseline; gap: 12px; }
    .rgv__title-main { font-size: 14px; font-weight: 600; }
    .rgv__sha-range { font-size: 11.5px; color: #94a3b8; display: flex; align-items: center; gap: 6px; }
    .rgv__sha-range code { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
    .rgv__arrow { color: #64748b; }
    .rgv__totals { display: flex; gap: 10px; font-size: 12px; color: #cbd5e1; }
    .rgv__total { color: #cbd5e1; }
    .rgv__add { color: #4ade80; }
    .rgv__del { color: #f87171; }
    .rgv__close { background: transparent; border: 1px solid rgba(148, 163, 184, 0.30); color: #e2e8f0; padding: 4px 10px; border-radius: 6px; cursor: pointer; }
    .rgv__close:hover { background: rgba(148, 163, 184, 0.12); }

    .rgv__body { flex: 1 1 auto; display: grid; grid-template-columns: 320px 1fr; min-height: 0; }

    .rgv__tree { border-right: 1px solid rgba(148, 163, 184, 0.18); overflow: auto; padding: 6px 0; background: rgba(15, 23, 42, 0.5); font-size: 12px; }
    .rgv__placeholder { padding: 16px; color: #94a3b8; font-style: italic; font-size: 12.5px; }
    .rgv__placeholder--error { color: #fca5a5; }

    .rgv__node { display: flex; align-items: center; gap: 6px; padding: 3px 12px 3px 8px; cursor: pointer; user-select: none; }
    .rgv__node:hover { background: rgba(148, 163, 184, 0.10); }
    .rgv__node--selected { background: rgba(125, 211, 252, 0.16); }
    .rgv__node-glyph { width: 16px; flex: 0 0 16px; color: #94a3b8; text-align: center; font-size: 11px; }
    .rgv__node-name { flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .rgv__node-stats { display: flex; gap: 6px; font-size: 11px; color: #94a3b8; flex: 0 0 auto; }
    .rgv__node-files { color: #94a3b8; }
    .rgv__node--folder .rgv__node-name { font-weight: 600; color: #e2e8f0; }
    .rgv__node--file .rgv__node-name { color: #cbd5e1; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 11.5px; }

    .rgv__status { display: inline-flex; align-items: center; justify-content: center; width: 14px; height: 14px; border-radius: 3px; font-size: 9px; font-weight: 700; background: rgba(148, 163, 184, 0.20); color: #cbd5e1; }
    .rgv__status[data-status="A"] { background: rgba(34, 197, 94, 0.30); color: #bbf7d0; }
    .rgv__status[data-status="M"] { background: rgba(56, 189, 248, 0.28); color: #bae6fd; }
    .rgv__status[data-status="D"] { background: rgba(220, 38, 38, 0.40); color: #fecaca; }
    .rgv__status[data-status="R"] { background: rgba(196, 181, 253, 0.30); color: #ede9fe; }
    .rgv__status[data-status="C"] { background: rgba(251, 191, 36, 0.30); color: #fde68a; }

    .rgv__diff { overflow: auto; display: flex; flex-direction: column; min-width: 0; }
    .rgv__diff-head { padding: 8px 14px; border-bottom: 1px solid rgba(148, 163, 184, 0.18); display: flex; align-items: center; gap: 10px; background: rgba(15, 23, 42, 0.6); }
    .rgv__diff-path { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 12px; color: #cbd5e1; flex: 1 1 auto; min-width: 0; overflow: hidden; text-overflow: ellipsis; }
    .rgv__copy { background: transparent; border: 1px solid rgba(148, 163, 184, 0.30); color: #cbd5e1; padding: 3px 8px; border-radius: 6px; cursor: pointer; font-size: 11.5px; }
    .rgv__copy:hover { background: rgba(148, 163, 184, 0.12); }

    .rgv__diff-body { margin: 0; padding: 12px 14px; flex: 1 1 auto; overflow: auto; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 12px; line-height: 1.55; white-space: pre; }
    .rgv__line { display: block; padding: 0 4px; }
    .rgv__line--add { background: rgba(34, 197, 94, 0.12); color: #bbf7d0; }
    .rgv__line--del { background: rgba(220, 38, 38, 0.14); color: #fecaca; }
    .rgv__line--hunk { color: #93c5fd; background: rgba(56, 189, 248, 0.08); }
    .rgv__line--meta { color: #94a3b8; }
    .rgv__line--ctx { color: #cbd5e1; }
  `]
})
export class RunGitViewerComponent {
  readonly job = input<JobInfo | null>(null);
  readonly run = input<RunRecord | null>(null);
  readonly visible = input<boolean>(false);

  readonly close = output<void>();

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

  readonly diffLines = computed(() => splitDiff(this.diffText()));

  private readonly jobService = inject(JobService);
  private currentLoadKey = '';

  constructor() {
    // Lazy-load when visible flips to true. We re-load if the (job, run)
    // identity changes between opens; same identity reuses the cache.
    // Effects on input signals fire after change detection.
    // Simpler: hook into a computed that triggers loadFiles via untracked.
    // To stay framework-friendly, do it from a setter-style effect:
  }

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
    this.jobService.getRunDiff(job.id, run.index, path, job.watchPath).subscribe({
      next: (res) => {
        this.diffText.set(res.diff ?? '');
        this.diffState.set('loaded');
      },
      error: (err) => {
        this.diffError.set(err?.error?.error || err?.message || 'Could not load diff.');
        this.diffState.set('error');
      }
    });
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
  text: string;
  kind: 'add' | 'del' | 'hunk' | 'meta' | 'ctx';
}

function splitDiff(raw: string): DiffLine[] {
  if (!raw) return [];
  const lines = raw.replace(/\r\n/g, '\n').split('\n');
  const out: DiffLine[] = [];
  for (const line of lines) {
    if (line.startsWith('@@')) out.push({ text: line, kind: 'hunk' });
    else if (line.startsWith('+++') || line.startsWith('---') || line.startsWith('diff ') || line.startsWith('index ') || line.startsWith('similarity ') || line.startsWith('rename ') || line.startsWith('new file ') || line.startsWith('deleted file ')) out.push({ text: line, kind: 'meta' });
    else if (line.startsWith('+')) out.push({ text: line, kind: 'add' });
    else if (line.startsWith('-')) out.push({ text: line, kind: 'del' });
    else out.push({ text: line, kind: 'ctx' });
  }
  return out;
}
