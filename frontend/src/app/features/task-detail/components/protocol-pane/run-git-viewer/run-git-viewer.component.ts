import {
  ChangeDetectionStrategy,
  Component,
  computed,
  inject,
  input,
  output,
  signal,
  DoCheck,
} from '@angular/core';
import { NgTemplateOutlet } from '@angular/common';
import type { TaskInfo } from '../../../../../models/task.model';
import type { RunFileChange, RunRecord } from '../../../../../features/run-timeline';
import { OverlayPortalDirective } from '../../../../../directives/overlay-portal.directive';
import { RunGitCacheService } from '../../../services/run-git-cache.service';
import { highlightBlock } from '../../beautiful-results/highlight-lazy';
import { perfMark, perfMeasure } from '../../../../../utils/perf-tracker';
import {
  buildTree,
  findFirstLeaf,
  splitDiff,
  detectLanguage,
  detectNewFile,
  type TreeNode,
  type DiffLine,
  type HighlightedLine,
} from './diff-utils';
import { isLargeDiff, describeDiffSize } from '../../../../../utils/large-diff-gate';

@Component({
  selector: 'app-run-git-viewer',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [NgTemplateOutlet, OverlayPortalDirective],
  templateUrl: './run-git-viewer.component.html',
  styleUrl: './run-git-viewer.component.scss',
})
export class RunGitViewerComponent implements DoCheck {
  readonly job = input<TaskInfo | null>(null);
  readonly run = input<RunRecord | null>(null);
  readonly visible = input<boolean>(false);

  readonly closeRequest = output<void>();

  // --- Per-run overlay state ---
  readonly files = signal<RunFileChange[]>([]);
  readonly filesState = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly filesError = signal<string | null>(null);
  readonly selectedPath = signal<string | null>(null);
  readonly diffText = signal<string>('');
  readonly diffState = signal<'idle' | 'loading' | 'loaded' | 'error'>('idle');
  readonly diffError = signal<string | null>(null);
  readonly expanded = signal<Set<string>>(new Set(['']));
  readonly copyState = signal<'idle' | 'copied' | 'failed'>('idle');

  readonly totalAdded = computed(() => this.files().reduce((s, f) => s + f.added, 0));
  readonly totalRemoved = computed(() => this.files().reduce((s, f) => s + f.removed, 0));
  readonly totalFiles = computed(() => this.files().length);
  readonly rootChildren = computed<TreeNode[]>(() => buildTree(this.files()));
  readonly diffLines = computed<DiffLine[]>(() => splitDiff(this.diffText()));
  readonly highlightedLines = signal<HighlightedLine[] | null>(null);
  readonly isNewFile = computed<boolean>(() => detectNewFile(this.diffText()));

  // Large-file gate (central threshold in utils/large-diff-gate): a huge
  // diff is not rendered automatically - building one DOM node per line
  // plus the per-line syntax-highlight pass is what makes the overlay
  // sluggish. Show a compact placeholder until the operator clicks
  // "Show diff"; reveal is remembered per-path for the session, with a
  // "show all" escape hatch.
  private readonly revealedPaths = signal<Set<string>>(new Set<string>());
  readonly revealAllLarge = signal(false);
  readonly diffIsLarge = computed<boolean>(() => isLargeDiff(this.diffText()));
  readonly diffSizeLabel = computed<string>(() => describeDiffSize(this.diffText()));
  readonly selectedFileStatus = computed<string>(() => {
    const path = this.selectedPath();
    if (!path) return '';
    return this.files().find((f) => f.path === path)?.status ?? '';
  });
  readonly diffGated = computed<boolean>(() => {
    if (!this.diffIsLarge()) return false;
    if (this.revealAllLarge()) return false;
    const path = this.selectedPath();
    return !(path && this.revealedPaths().has(path));
  });

  private readonly runGitCache = inject(RunGitCacheService);
  private currentLoadKey = '';

  ngDoCheck(): void {
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
    return s === 'copied' ? '✓ Copied' : s === 'failed' ? '⚠ Copy failed' : '📋 Copy diff';
  }

  copyDiff(): void {
    const text = this.diffText();
    if (!text) return;
    navigator.clipboard?.writeText(text).then(
      () => { this.copyState.set('copied'); setTimeout(() => this.copyState.set('idle'), 2000); },
      () => { this.copyState.set('failed'); setTimeout(() => this.copyState.set('idle'), 2000); },
    );
  }

  short(sha: string | null): string {
    return !sha ? '' : sha.length > 12 ? sha.slice(0, 8) : sha;
  }

  onNodeClick(node: TreeNode): void {
    if (node.isFolder) {
      const next = new Set(this.expanded());
      if (next.has(node.fullPath)) next.delete(node.fullPath); else next.add(node.fullPath);
      this.expanded.set(next);
      return;
    }
    if (!node.change || this.selectedPath() === node.fullPath) return;
    this.selectedPath.set(node.fullPath);
    this.loadDiff(node.fullPath);
  }

  private loadFiles(): void {
    const job = this.job(), run = this.run();
    if (!job || !run) return;
    perfMark('run-files-fetch');
    this.filesState.set('loading');
    this.filesError.set(null);
    this.selectedPath.set(null);
    this.diffText.set('');
    this.diffState.set('idle');
    this.highlightedLines.set(null);
    this.runGitCache.getFiles(job.id, run.index, job.watchPath).subscribe({
      next: (res) => {
        this.files.set(res.files ?? []);
        const e = new Set<string>(['']);
        for (const f of res.files ?? []) {
          const segs = f.path.split('/');
          for (let i = 1; i < segs.length; i++) e.add(segs.slice(0, i).join('/'));
          break;
        }
        this.expanded.set(e);
        this.filesState.set('loaded');
        perfMark('run-files-rendered');
        perfMeasure('run-files-fetch-to-rendered', 'run-files-fetch', 'run-files-rendered');
        const leaf = findFirstLeaf(this.rootChildren());
        if (leaf) { this.selectedPath.set(leaf.fullPath); this.loadDiff(leaf.fullPath); }
      },
      error: (err) => {
        this.filesError.set(err?.error?.error || err?.message || 'Could not load files.');
        this.filesState.set('error');
      },
    });
  }

  revealCurrentDiff(): void {
    const path = this.selectedPath();
    if (!path) return;
    const next = new Set(this.revealedPaths());
    next.add(path);
    this.revealedPaths.set(next);
    this.highlightCurrentDiff(path);
  }

  revealAll(): void {
    this.revealAllLarge.set(true);
    const path = this.selectedPath();
    if (path) this.highlightCurrentDiff(path);
  }

  private loadDiff(path: string): void {
    const job = this.job(), run = this.run();
    if (!job || !run) return;
    perfMark('run-diff-fetch');
    this.diffState.set('loading');
    this.diffError.set(null);
    this.diffText.set('');
    this.highlightedLines.set(null);
    this.runGitCache.getDiff(job.id, run.index, path, job.watchPath).subscribe({
      next: (res) => {
        this.diffText.set(res.diff ?? '');
        this.diffState.set('loaded');
        perfMark('run-diff-rendered');
        perfMeasure('run-diff-fetch-to-rendered', 'run-diff-fetch', 'run-diff-rendered');
        // Skip the per-line highlight pass while the diff is gated; it
        // runs once the operator reveals it.
        if (!this.diffGated()) this.highlightCurrentDiff(path);
      },
      error: (err) => {
        this.diffError.set(err?.error?.error || err?.message || 'Could not load diff.');
        this.diffState.set('error');
      },
    });
  }

  private async highlightCurrentDiff(forPath: string): Promise<void> {
    const lang = detectLanguage(forPath);
    if (!lang) { this.highlightedLines.set(null); return; }
    const lines = this.diffLines();
    const tasks = lines.map(async (line) => {
      if (line.kind === 'add' || line.kind === 'del' || line.kind === 'ctx') {
        const { html } = await highlightBlock(line.body, lang);
        return { ...line, highlightedHtml: html };
      }
      return { ...line, highlightedHtml: null };
    });
    const highlighted = await Promise.all(tasks);
    if (this.selectedPath() === forPath) this.highlightedLines.set(highlighted);
  }
}
