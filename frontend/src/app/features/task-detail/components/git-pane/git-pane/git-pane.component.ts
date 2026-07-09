import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { GitPaneService } from '../../../services/git-pane.service';
import { LayoutPanesService } from '../../../services/layout-panes.service';
import { GitFileTreeComponent } from '../git-file-tree/git-file-tree.component';
import type { TaskCommitInfo } from '../../../../git';
import type { CodeReviewListEntry } from '../../../../../services/task.service';
import {
  codeReviewVerdictGlyph,
  codeReviewVerdictLabel,
  codeReviewVerdictTone,
  type CodeReviewVerdictTone,
} from '../../code-review-verdict.util';

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
  private readonly layout = inject(LayoutPanesService);
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
   * Master collapse for the whole commit-meta head (landed ladder +
   * "N task commits" group + per-commit banner) that sits above the
   * tree/diff split. Collapsing it hands all the vertical space to the
   * tree + diff so the review surface reads compact; persisted so the
   * operator's preference survives a reload. Default expanded so the
   * ladder / commit context stays visible for first-time viewers.
   */
  readonly headCollapsed = signal<boolean>(readHeadCollapsed());

  toggleHeadCollapsed(): void {
    const next = !this.headCollapsed();
    this.headCollapsed.set(next);
    writeHeadCollapsed(next);
  }

  /** Compact label for the collapsed head strip. */
  readonly headTitle = computed<string>(() => {
    const n = this.git.commitChain().length;
    return n > 1 ? `${n} task commits` : 'Commit details';
  });

  /**
   * Diff render layout. Side-by-side shows the before/after columns; the
   * unified (inline) mode collapses to a single "just the change" column
   * per the operator's ask. Persisted; default side-by-side. This is now
   * the single source of truth for the diff2html `outputFormat` - the
   * previous behaviour (implicitly side-by-side only while maximized,
   * line-by-line otherwise) is replaced by this explicit, remembered
   * toggle so maximizing no longer silently changes the layout.
   */
  readonly diffViewMode = signal<DiffViewMode>(readDiffViewMode());

  toggleDiffViewMode(): void {
    const next: DiffViewMode = this.diffViewMode() === 'side-by-side' ? 'line-by-line' : 'side-by-side';
    this.diffViewMode.set(next);
    writeDiffViewMode(next);
  }

  // --- Tree | diff splitter (draggable, persisted) -----------------------
  // In the split (pane-maximized) layout the file-change tree sits left of
  // the diff. The divider between them is draggable; its width is stored in
  // px and pushed to the tree column through the `--git-tree-width` custom
  // property so the SCSS keeps the min/behaviour in one place. Clamp math
  // lives in `clampTreeWidth` so the drag can never squeeze either side
  // below its readable floor.
  readonly treeColWidth = signal<number>(readTreeWidth());
  readonly treeResizing = signal(false);
  private treeResize: { pointerId: number; container: HTMLElement } | null = null;

  startTreeResize(event: PointerEvent): void {
    const splitter = event.currentTarget as HTMLElement;
    const container = splitter.parentElement;
    if (!container) return;
    event.preventDefault();
    splitter.setPointerCapture(event.pointerId);
    this.treeResize = { pointerId: event.pointerId, container };
    this.treeResizing.set(true);
    document.body.style.cursor = 'col-resize';
  }

  onTreeResizeMove(event: PointerEvent): void {
    const drag = this.treeResize;
    if (!drag || drag.pointerId !== event.pointerId) return;
    const rect = drag.container.getBoundingClientRect();
    this.treeColWidth.set(clampTreeWidth(event.clientX - rect.left, rect.width));
  }

  endTreeResize(event: PointerEvent): void {
    const drag = this.treeResize;
    if (!drag || drag.pointerId !== event.pointerId) return;
    (event.currentTarget as HTMLElement).releasePointerCapture(event.pointerId);
    this.treeResize = null;
    this.treeResizing.set(false);
    document.body.style.cursor = '';
    writeTreeWidth(this.treeColWidth());
  }

  onTreeResizeKey(event: KeyboardEvent): void {
    if (event.key !== 'ArrowLeft' && event.key !== 'ArrowRight') return;
    event.preventDefault();
    const container = (event.currentTarget as HTMLElement).parentElement;
    const width = container?.getBoundingClientRect().width ?? 0;
    const step = event.key === 'ArrowLeft' ? -TREE_RESIZE_STEP : TREE_RESIZE_STEP;
    this.treeColWidth.set(clampTreeWidth(this.treeColWidth() + step, width));
    writeTreeWidth(this.treeColWidth());
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
    const sideBySide = this.diffViewMode() === 'side-by-side';
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

  // --- Commit-row code-review rating badge (AGT-1995) --------------------
  // A compact indicator of the code-review verdict for the commit currently
  // shown on the commit line. Tone/label/glyph come from the shared verdict
  // util so this stays in lockstep with the Code Review tab; the review data
  // itself lives on GitPaneService.commitReview().

  reviewTone(verdict: string | null | undefined): CodeReviewVerdictTone {
    return codeReviewVerdictTone(verdict);
  }

  reviewLabel(verdict: string | null | undefined): string {
    return codeReviewVerdictLabel(verdict);
  }

  reviewGlyph(verdict: string | null | undefined): string {
    return codeReviewVerdictGlyph(verdict);
  }

  /** Tooltip for the rating badge: verdict + one-line summary + affordance. */
  reviewTooltip(review: CodeReviewListEntry): string {
    const label = codeReviewVerdictLabel(review.verdict);
    const summary = (review.summary ?? '').trim();
    const head = summary ? `Code review: ${label} · ${summary}` : `Code review: ${label}`;
    return `${head}. Click to open the Code Review tab.`;
  }

  /**
   * Reveal the prompt pane (if hidden) and focus its Code Review tab. Routed
   * through the shared layout service rather than an @Output so the git pane
   * does not need the task-detail shell to mediate a same-feature navigation.
   */
  openCodeReview(): void {
    this.layout.openPromptTab('code-review');
  }
}

export type DiffViewMode = 'side-by-side' | 'line-by-line';

const COMMIT_HEADER_COLLAPSED_KEY = 'taskboard.gitPane.commitHeaderCollapsed';
const COMMIT_GROUP_COLLAPSED_KEY = 'taskboard.gitPane.commitGroupCollapsed';
const HEAD_COLLAPSED_KEY = 'taskboard.gitPane.headCollapsed';
const DIFF_VIEW_MODE_KEY = 'taskboard.gitPane.diffViewMode';
const TREE_WIDTH_KEY = 'taskboard.gitPane.treeWidth';

// Splitter clamp: the tree may not drop below MIN_TREE_PX nor squeeze the
// diff below MIN_DIFF_PX. Keyboard arrows nudge by TREE_RESIZE_STEP. The
// tree floor mirrors the SCSS `min-width` on `.git-view__tree-col` so the
// CSS minimum never fights the flex-basis mid-drag (the spring-back bug the
// pane splitter documents in layout-panes.service).
const MIN_TREE_PX = 200;
const MIN_DIFF_PX = 320;
const TREE_WIDTH_DEFAULT = 300;
const TREE_RESIZE_STEP = 16;

/** Clamp a proposed tree width against its floor and the diff's floor. */
export function clampTreeWidth(raw: number, containerWidth: number): number {
  const upper = containerWidth > 0 ? Math.max(MIN_TREE_PX, containerWidth - MIN_DIFF_PX) : Number.POSITIVE_INFINITY;
  return Math.round(Math.max(MIN_TREE_PX, Math.min(upper, raw)));
}

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

function readHeadCollapsed(): boolean {
  try { return localStorage.getItem(HEAD_COLLAPSED_KEY) === '1'; }
  catch { return false; }
}

function writeHeadCollapsed(value: boolean): void {
  try { localStorage.setItem(HEAD_COLLAPSED_KEY, value ? '1' : '0'); }
  catch { /* ignore quota / privacy-mode errors */ }
}

function readDiffViewMode(): DiffViewMode {
  try {
    return localStorage.getItem(DIFF_VIEW_MODE_KEY) === 'line-by-line' ? 'line-by-line' : 'side-by-side';
  }
  catch { return 'side-by-side'; }
}

function writeDiffViewMode(value: DiffViewMode): void {
  try { localStorage.setItem(DIFF_VIEW_MODE_KEY, value); }
  catch { /* ignore quota / privacy-mode errors */ }
}

function readTreeWidth(): number {
  try {
    const raw = Number(localStorage.getItem(TREE_WIDTH_KEY));
    if (Number.isFinite(raw) && raw >= MIN_TREE_PX) return Math.round(raw);
  }
  catch { /* ignore */ }
  return TREE_WIDTH_DEFAULT;
}

function writeTreeWidth(value: number): void {
  try { localStorage.setItem(TREE_WIDTH_KEY, String(Math.round(value))); }
  catch { /* ignore quota / privacy-mode errors */ }
}
