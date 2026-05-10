import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { GitPaneService } from '../../services/git-pane.service';
import { GitFileTreeComponent } from './git-file-tree.component';

// Cycle 7f: diff2html (~120 KB minified, includes its own theme CSS) is
// loaded lazily the first time a non-empty diff arrives. The pre-Cycle-7f
// import was static, which dragged the whole library into the initial
// chunk even though most users never open the git pane on first paint.
// The lazy module + dark-color-scheme constant are cached after first
// load so the second diff render is synchronous.
// We hold the dynamically-imported modules behind `any` to keep this
// component free of compile-time references to diff2html types - those
// types only land in the bundle if you import them. The shape we need
// is narrow (one function + one enum value).
let diff2htmlModuleCache: { html: (diff: string, opts: any) => string; darkScheme: number } | null = null;
async function loadDiff2Html(): Promise<typeof diff2htmlModuleCache> {
  if (diff2htmlModuleCache) return diff2htmlModuleCache;
  const [main, types] = await Promise.all([
    import('diff2html'),
    import('diff2html/lib-esm/types'),
  ]);
  diff2htmlModuleCache = { html: main.html as any, darkScheme: types.ColorSchemeType.DARK as unknown as number };
  return diff2htmlModuleCache;
}

/**
 * Renders the Git pane of the job-detail view: working-tree status,
 * per-file diff, and commit form. State + API calls live in
 * GitPaneService (provided locally on JobDetailComponent); this
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
  imports: [DatePipe, GitFileTreeComponent],
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
}
