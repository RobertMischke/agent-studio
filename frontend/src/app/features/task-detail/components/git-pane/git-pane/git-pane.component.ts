import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { GitPaneService } from '../../../services/git-pane.service';
import { GitFileTreeComponent } from '../git-file-tree/git-file-tree.component';
import type { TaskCommitInfo } from '../../../../git';

import { TooltipDirective } from 'coding-agent-chat/shared';
import { formatCompactDateTime, formatDateTime } from '../../../../../services/format.util';
import { isLargeDiff, describeDiffSize } from '../../../../../utils/large-diff-gate';
import { currentDiff2Html, hasDiff2HtmlLoaded, loadDiff2Html } from '../../../../../utils/diff2html-lazy';
// Cycle 7f: diff2html (~120 KB minified, includes its own theme CSS) is
// loaded lazily the first time a non-empty diff arrives. The pre-Cycle-7f
// import was static, which dragged the whole library into the initial
// chunk even though most users never open the git pane on first paint.
// The lazy module + dark-color-scheme constant are cached after first
// load so the second diff render is synchronous.
// The loader itself lives in utils so every diff surface shares the same
// cached module and the same large-diff gate can suppress that import.

/**
 * Renders the Git pane of the job-detail view: working-tree status,
 * per-file diff, and commit form. State + API calls live in
 * GitPaneService (provided locally on TaskDetailComponent); this
 * component is purely presentational.
 *
 * The selected file's unified-diff text is rendered through
 * `diff2html` so users see syntax-aware add/remove highlighting and
 * (when maximized) a side-by-side view. The diff section has its own
 * maximize toggle independent of the surrounding pane: in-pane it uses
 * `line-by-line` to fit the narrow column, and switches to
 * `side-by-side` when the diff is fullscreened.
 */
@Component({
  selector: 'app-git-pane',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [DatePipe, GitFileTreeComponent, TooltipDirective],
  templateUrl: './git-pane.component.html',
  styleUrls: ['./git-pane.component.scss']
})
export class GitPaneComponent {
  /** Whether this pane is currently the maximized one. */
  readonly maximized = input(false);
  /** Flex weight to apply when not maximized. */
  readonly weight = input<number>(1);
  /** Whether the job's CLI is currently running — disables commit/generate. */
  readonly isRunning = input(false);
  /**
   * Whether this job is the runner's currently-active job for its
   * project. Worktree-isolation rule: the working tree is shared
   * across the whole repository, so live `git status` output belongs to
   * whichever task the agent is currently editing - i.e. the active
   * one. On non-active tasks this pane suppresses the working-tree
   * view entirely and renders a placeholder; only `git show <sha>`-
   * derived diffs from this task's own commits are still shown.
   */
  readonly isActiveJob = input<boolean>(false);

  readonly maximizeToggle = output<void>();
  readonly hide = output<void>();

  readonly git = inject(GitPaneService);
  private readonly sanitizer = inject(DomSanitizer);

  /** Diff section fullscreen toggle, scoped to this component. */
  readonly diffMaximized = signal(false);

  /**
   * Collapse toggle for the commit-message banner above the tree+diff
   * split. Persisted in localStorage so the operator's preference
   * survives a reload. One toggle drives both the in-pane and
   * fullscreened layouts so the mental model stays simple.
   */
  readonly commitHeaderCollapsed = signal<boolean>(readCommitHeaderCollapsed());
  readonly commitGroupCollapsed = signal<boolean>(readCommitGroupCollapsed());

  toggleCommitHeaderCollapsed(): void {
    const next = !this.commitHeaderCollapsed();
    this.commitHeaderCollapsed.set(next);
    writeCommitHeaderCollapsed(next);
  }

  toggleCommitGroupCollapsed(): void {
    const next = !this.commitGroupCollapsed();
    this.commitGroupCollapsed.set(next);
    writeCommitGroupCollapsed(next);
  }

  /**
   * Title rendered in the pane header. Surfaces the multi-commit case
   * ("3 task commits") so the user sees at a glance that the chain has
   * more than one entry.
   */
  readonly paneTitle = computed<string>(() => {
    if (this.git.viewMode() === 'commit') {
      const n = this.git.commitChain().length;
      if (n > 1) return `⏺ ${n} task commits`;
      return '⏺ Task commit';
    }
    return '⎇ Git view';
  });

  /** Tracks whether the diff2html module has finished its dynamic import. */
  private readonly diff2htmlReady = signal(hasDiff2HtmlLoaded());

  // Trigger the lazy import the first time we're asked to render a diff.
  // Until the module is in memory, diffHtml() returns null and the
  // template shows a small placeholder; the moment the import resolves
  // the signal flips and the computed re-runs synchronously.
  private readonly _ensureDiff2HtmlLoaded = effect(() => {
    const text = this.git.diffText();
    if (!text) return;
    if (this.diffGated()) return;
    if (this.diff2htmlReady()) return;
    loadDiff2Html().then(() => this.diff2htmlReady.set(true));
  });

  /**
   * Large-file gate (central threshold in utils/large-diff-gate). Big
   * diffs are not auto-rendered - the full diff2html render of a huge
   * block is what makes the pane feel slow - so a compact placeholder is
   * shown until the operator clicks "Show diff". Reveal is remembered
   * per-path for the session, plus a "show all" escape hatch.
   */
  private readonly revealedPaths = signal<Set<string>>(new Set<string>());
  readonly revealAllLargeDiffs = signal(false);

  readonly diffIsLarge = computed<boolean>(() => isLargeDiff(this.git.diffText()));
  readonly diffSizeLabel = computed<string>(() => describeDiffSize(this.git.diffText()));

  /** Status char (A/M/D/…) of the selected file, for the gated placeholder. */
  readonly selectedFileStatus = computed<string>(() => {
    const path = this.git.selectedDiffPath();
    if (!path) return '';
    const fromCommit = this.git.commitFiles().find((f) => f.path === path);
    if (fromCommit) return fromCommit.status;
    const fromStatus = this.git.status()?.files.find((f) => f.path === path);
    return fromStatus?.status ?? '';
  });

  /** True when the current diff is large and the operator hasn't revealed it. */
  readonly diffGated = computed<boolean>(() => {
    if (!this.diffIsLarge()) return false;
    if (this.revealAllLargeDiffs()) return false;
    const path = this.git.selectedDiffPath();
    return !(path && this.revealedPaths().has(path));
  });

  revealCurrentDiff(): void {
    const path = this.git.selectedDiffPath();
    if (!path) return;
    const next = new Set(this.revealedPaths());
    next.add(path);
    this.revealedPaths.set(next);
  }

  revealAll(): void {
    this.revealAllLargeDiffs.set(true);
  }

  readonly diffHtml = computed<SafeHtml | null>(() => {
    const text = this.git.diffText();
    if (!text) return null;
    if (this.diffGated()) return null;
    const diff2html = currentDiff2Html();
    if (!this.diff2htmlReady() || !diff2html) return null;
    const sideBySide = this.maximized() || this.diffMaximized();
    const rendered = diff2html.html(text, {
      drawFileList: false,
      outputFormat: sideBySide ? 'side-by-side' : 'line-by-line',
      matching: 'lines',
      colorScheme: diff2html.darkScheme,
    });
    return this.sanitizer.bypassSecurityTrustHtml(rendered);
  });

  toggleDiffMaximize(): void {
    this.diffMaximized.update(v => !v);
  }

  commitChainTooltip(entry: TaskCommitInfo, index: number): string {
    return `${index + 1}/${this.git.commitChain().length} · ${entry.shortSha} · ${formatDateTime(entry.at)} · ${entry.message}`;
  }

  commitChainTimestamp(entry: TaskCommitInfo): string {
    return formatCompactDateTime(entry.at);
  }

  selectedCommitSummary(): string {
    if (this.git.isAggregate()) {
      const files = this.git.commitFiles().length;
      const commits = this.git.commitChain().length;
      return `All ${commits} commits · ${files} ${files === 1 ? 'file' : 'files'}`;
    }
    const sha = this.git.selectedCommitSha();
    const entry = this.git.commitChain().find(c => c.sha === sha);
    if (!entry) return `${this.git.commitChain().length} task commits`;
    return `${entry.shortSha} · ${entry.message.split('\n')[0]}`;
  }

  // --- Commit-provenance / landed-ladder (ASS-1724) ---------------------
  // The landed ladder (task/<id> -> develop -> main) is derived live off the
  // git graph by the backend and read through git.provenance(). It is the
  // single place the task's develop-merged / main-pending state is shown
  // (UI-feedback 2026-07-09: the redundant "Merged to develop" pill and the
  // per-commit "on develop" membership chips were removed). Each rung carries
  // its own reached/pending tooltip inline in the template.

  /** Short-SHA display; em-dash placeholder when a rung has no resolved HEAD. */
  short(sha: string | null | undefined): string {
    if (!sha) return '—';
    return sha.length > 7 ? sha.slice(0, 7) : sha;
  }
}

const COMMIT_HEADER_COLLAPSED_KEY = 'taskboard.gitPane.commitHeaderCollapsed';
const COMMIT_GROUP_COLLAPSED_KEY = 'taskboard.gitPane.commitGroupCollapsed';

function readCommitHeaderCollapsed(): boolean {
  try { return localStorage.getItem(COMMIT_HEADER_COLLAPSED_KEY) === '1'; }
  catch { return false; }
}

function writeCommitHeaderCollapsed(value: boolean): void {
  try { localStorage.setItem(COMMIT_HEADER_COLLAPSED_KEY, value ? '1' : '0'); }
  catch { /* ignore quota / privacy-mode errors */ }
}

function readCommitGroupCollapsed(): boolean {
  try {
    const stored = localStorage.getItem(COMMIT_GROUP_COLLAPSED_KEY);
    return stored === null ? true : stored === '1';
  }
  catch { return true; }
}

function writeCommitGroupCollapsed(value: boolean): void {
  try { localStorage.setItem(COMMIT_GROUP_COLLAPSED_KEY, value ? '1' : '0'); }
  catch { /* ignore quota / privacy-mode errors */ }
}
