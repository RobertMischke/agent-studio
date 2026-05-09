import { ChangeDetectionStrategy, Component, computed, inject, input, output, signal } from '@angular/core';
import { DatePipe } from '@angular/common';
import { DomSanitizer, SafeHtml } from '@angular/platform-browser';
import { html as diff2html } from 'diff2html';
import { ColorSchemeType } from 'diff2html/lib-esm/types';
import { GitPaneService } from '../git-pane.service';
import { GitFileTreeComponent } from './git-file-tree.component';

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

  readonly diffHtml = computed<SafeHtml | null>(() => {
    const text = this.git.diffText();
    if (!text) return null;
    const sideBySide = this.maximized() || this.diffMaximized();
    const rendered = diff2html(text, {
      drawFileList: false,
      outputFormat: sideBySide ? 'side-by-side' : 'line-by-line',
      matching: 'lines',
      colorScheme: ColorSchemeType.DARK,
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
