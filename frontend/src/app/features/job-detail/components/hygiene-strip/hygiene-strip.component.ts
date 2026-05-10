import { ChangeDetectionStrategy, Component, OnDestroy, computed, effect, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import type { JobInfo } from '../../../../models/job.model';
import type { GitHygieneStatus } from '../../../../features/git';
import { GitHygieneService } from '../../../../services/git-hygiene.service';
import { ErrorDialogService } from '../../../../services/error-dialog.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';

/**
 * Repository-hygiene strip rendered at the top of the protocol pane for
 * jobs in `4-auto-review`, `5-human-review`, `6-completed`, or `7-archive`.
 *
 * The strip is the calm, angular icon-only variant introduced by the
 * "task detail page chat-first" redesign: three tiny squares (commit /
 * tree / push) carry the same data the verbose strip used to show, with
 * hover-tooltips for the breakdown. The full warning banners
 * ("accepted task work uncommitted", "push pending") still expand
 * inline because those carry user actions, not just status.
 */
@Component({
  selector: 'app-hygiene-strip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  templateUrl: './hygiene-strip.component.html',
  styleUrls: ['./hygiene-strip.component.scss']
})
export class HygieneStripComponent implements OnDestroy {
  readonly job = input.required<JobInfo>();
  /**
   * Whether this job is the runner's currently-active job for its
   * project. Worktree-isolation rule: the "Accepted task work is
   * sitting uncommitted" warning only fires when the job owns whatever
   * the agent is currently editing. The backend's
   * <c>acceptedTaskUncommitted</c> flag is already gated on this on the
   * data side; we belt-and-suspender it here so the warning never
   * shows on a non-active task even if the cached hygiene snapshot
   * arrived before the runner status flip.
   */
  readonly isActiveJob = input<boolean>(false);

  private readonly hygieneSvc = inject(GitHygieneService);
  private readonly errorDialog = inject(ErrorDialogService);

  readonly hygiene = signal<GitHygieneStatus | null>(null);
  readonly committing = signal(false);

  // Lanes where we want the strip visible. Pre-progress lanes don't
  // produce committable evidence so the chip would be noise.
  private static readonly VISIBLE_STATES = new Set([
    '4-auto-review', '5-human-review', '6-completed', '7-archive'
  ]);

  readonly visibleForState = computed(() => HygieneStripComponent.VISIBLE_STATES.has(this.job().state));

  readonly commitTooltip = computed(() => {
    const h = this.hygiene();
    if (!h) return 'Repository commit state — loading…';
    if (h.job?.jobInfoCommitPresent) {
      const sha = h.job.stampedCommitSha ? this.shortSha(h.job.stampedCommitSha) : '';
      return sha ? `Task committed (${sha})` : 'Task committed';
    }
    return 'No task commit recorded';
  });

  readonly treeTooltip = computed(() => {
    const h = this.hygiene();
    if (!h) return 'Working tree — loading…';
    if (!h.isDirty) return 'Working tree clean';
    return `Working tree dirty — ${h.stagedCount} staged · ${h.unstagedCount} unstaged · ${h.untrackedCount} untracked`;
  });

  readonly pushTooltip = computed(() => {
    const h = this.hygiene();
    if (!h) return 'Push state — loading…';
    if (!h.hasUpstream) return 'No upstream configured';
    if (h.ahead > 0) return `${h.ahead} commit${h.ahead === 1 ? '' : 's'} ahead — push pending (${h.upstream})`;
    return `In sync with ${h.upstream}`;
  });

  pushIconChar(h: GitHygieneStatus): string {
    if (!h.hasUpstream) return '·';
    if (h.ahead > 0) return '↑';
    return '✓';
  }

  private pollTimer: VisibleIntervalHandle | null = null;
  private currentJobKey: string | null = null;

  constructor() {
    effect(() => {
      const j = this.job();
      const visible = this.visibleForState();
      const key = `${j.watchPath}::${j.id}`;
      if (!visible) {
        this.stopPolling();
        this.hygiene.set(null);
        this.currentJobKey = null;
        return;
      }
      if (key === this.currentJobKey) return;
      this.currentJobKey = key;
      this.startPolling(j);
    });
  }

  ngOnDestroy(): void {
    this.stopPolling();
  }

  shortSha(sha: string | null | undefined): string {
    return sha ? sha.slice(0, 7) : '';
  }

  commitAccepted(): void {
    const j = this.job();
    if (!j || this.committing()) return;
    this.committing.set(true);
    this.hygieneSvc.commitAcceptedEvidence(j.id, j.watchPath).subscribe({
      next: () => {
        this.committing.set(false);
        this.refreshOnce(j);
      },
      error: (err) => {
        this.committing.set(false);
        this.errorDialog.show(err, { title: 'Commit accepted evidence failed', source: `Task ${j.id}` });
      }
    });
  }

  private startPolling(j: JobInfo): void {
    this.stopPolling();
    this.refreshOnce(j);
    this.pollTimer = setVisibleInterval(() => this.refreshOnce(j), 15_000);
  }

  private refreshOnce(j: JobInfo): void {
    this.hygieneSvc.fetchForJob(j.id, j.watchPath).subscribe({
      next: (s) => this.hygiene.set(s),
      error: () => { /* keep last snapshot */ }
    });
  }

  private stopPolling(): void {
    if (this.pollTimer) {
      clearVisibleInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }
}
