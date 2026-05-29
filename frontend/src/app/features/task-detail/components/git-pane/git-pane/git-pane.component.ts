import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { GitPaneService } from '../../../services/git-pane.service';
import { GitFileTreeComponent } from '../git-file-tree/git-file-tree.component';
import type { TaskCommitInfo, TaskExcludedCommitInfo } from '../../../../git';

import { TooltipDirective } from '../../../../../components/tooltip';
// Cycle 7f: diff2html (~120 KB minified, includes its own theme CSS) is
// loaded lazily the first time a non-empty diff arrives. The pre-Cycle-7f
// import was static, which dragged the whole library into the initial
// chunk even though most users never open the git pane on first paint.
// The lazy module + dark-color-scheme constant are cached after first
// load so the second diff render is synchronous.
// We hold the dynamically-imported modules behind a narrow local type so
// the component stays free of static diff2html imports. The shape we need
// is one function plus the dark color-scheme enum value.
interface Diff2HtmlOptions {
  drawFileList: boolean;
  outputFormat: 'line-by-line' | 'side-by-side';
  matching: 'lines';
  colorScheme: number;
}
type Diff2HtmlRenderer = (diff: string, opts: Diff2HtmlOptions) => string;
let diff2htmlModuleCache: { html: Diff2HtmlRenderer; darkScheme: number } | null = null;
async function loadDiff2Html(): Promise<typeof diff2htmlModuleCache> {
  if (diff2htmlModuleCache) return diff2htmlModuleCache;
  const [main, types] = await Promise.all([
    import('diff2html'),
    import('diff2html/lib-esm/types'),
  ]);
  diff2htmlModuleCache = { html: main.html as unknown as Diff2HtmlRenderer, darkScheme: types.ColorSchemeType.DARK as unknown as number };
  return diff2htmlModuleCache;
}

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

  toggleCommitHeaderCollapsed(): void {
    const next = !this.commitHeaderCollapsed();
    this.commitHeaderCollapsed.set(next);
    writeCommitHeaderCollapsed(next);
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
  private readonly diff2htmlReady = signal(diff2htmlModuleCache !== null);

  // Trigger the lazy import the first time we're asked to render a diff.
  // Until the module is in memory, diffHtml() returns null and the
  // template shows a small placeholder; the moment the import resolves
  // the signal flips and the computed re-runs synchronously.
  private readonly _ensureDiff2HtmlLoaded = effect(() => {
    const text = this.git.diffText();
    if (!text) return;
    if (this.diff2htmlReady()) return;
    loadDiff2Html().then(() => this.diff2htmlReady.set(true));
  });

  readonly diffHtml = computed<SafeHtml | null>(() => {
    const text = this.git.diffText();
    if (!text) return null;
    if (!this.diff2htmlReady() || !diff2htmlModuleCache) return null;
    const sideBySide = this.maximized() || this.diffMaximized();
    const rendered = diff2htmlModuleCache.html(text, {
      drawFileList: false,
      outputFormat: sideBySide ? 'side-by-side' : 'line-by-line',
      matching: 'lines',
      colorScheme: diff2htmlModuleCache.darkScheme,
    });
    return this.sanitizer.bypassSecurityTrustHtml(rendered);
  });

  setCommitMessage(value: string): void {
    this.git.commitMessage.set(value);
  }

  toggleDiffMaximize(): void {
    this.diffMaximized.update(v => !v);
  }

  commitChainTooltip(entry: TaskCommitInfo, index: number): string {
    return `${index + 1}/${this.git.commitChain().length} · ${entry.shortSha} · ${entry.message}`;
  }

  /** Expander state for the "(N excluded)" list. Collapsed by default. */
  readonly excludedExpanded = signal(false);

  toggleExcluded(): void {
    this.excludedExpanded.update((v) => !v);
  }

  /**
   * True for commits the operator added or restored by hand. The chain gets
   * a small marker so a reviewer can tell rule-driven from hand-curated
   * attributions at a glance.
   */
  isManualAttribution(entry: TaskCommitInfo): boolean {
    return (
      entry.attribution === 'manual-add' ||
      entry.attribution === 'manual-include-after-exclude'
    );
  }

  /** Short marker label for a manual attribution; empty for automatic/legacy. */
  attributionMarker(entry: TaskCommitInfo): string {
    switch (entry.attribution) {
      case 'manual-add':
        return '+ added';
      case 'manual-include-after-exclude':
        return '↩ restored';
      default:
        return '';
    }
  }

  /** Confidence as a whole-percent string (e.g. "90%"); empty when absent. */
  confidencePercent(entry: TaskCommitInfo): string {
    if (entry.confidence == null) return '';
    return `${Math.round(entry.confidence * 100)}%`;
  }

  /** Hover text spelling out attribution kind + confidence for a chain entry. */
  attributionTooltip(entry: TaskCommitInfo): string {
    const kind =
      entry.attribution === 'manual-add'
        ? 'Manually added by operator'
        : entry.attribution === 'manual-include-after-exclude'
          ? 'Manually restored after exclusion'
          : entry.attribution === 'automatic'
            ? 'Attributed automatically by the rule engine'
            : 'Legacy attribution (pre-rule-engine)';
    const pct = this.confidencePercent(entry);
    return pct ? `${kind} · confidence ${pct}` : kind;
  }

  /** Human-readable label for an exclusion reason code. */
  exclusionReasonLabel(reason: string): string {
    switch (reason) {
      case 'crash-recovery-of-other-task':
        return 'Crash-recovery for another task';
      case 'update-stable-bump':
        return 'Update-stable / submodule bump';
      case 'merge-commit':
        return 'Merge commit';
      case 'outside-task-window':
        return 'Outside the task session window';
      case 'manual-exclude':
        return 'Excluded by operator';
      default:
        return 'Other';
    }
  }

  excludedTooltip(entry: TaskExcludedCommitInfo): string {
    const subject = entry.subject ? ` · ${entry.subject}` : '';
    return `${entry.shortSha} · ${this.exclusionReasonLabel(entry.reason)}${subject}`;
  }
}

const COMMIT_HEADER_COLLAPSED_KEY = 'taskboard.gitPane.commitHeaderCollapsed';

function readCommitHeaderCollapsed(): boolean {
  try { return localStorage.getItem(COMMIT_HEADER_COLLAPSED_KEY) === '1'; }
  catch { return false; }
}

function writeCommitHeaderCollapsed(value: boolean): void {
  try { localStorage.setItem(COMMIT_HEADER_COLLAPSED_KEY, value ? '1' : '0'); }
  catch { /* ignore quota / privacy-mode errors */ }
}
