import { ChangeDetectionStrategy, Component, OnDestroy, computed, effect, inject, input, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { GitHygieneStatus, JobInfo } from '../../../models/job.model';
import { GitHygieneService } from '../../../services/git-hygiene.service';
import { ErrorDialogService } from '../../../services/error-dialog.service';

/**
 * Repository-hygiene strip rendered at the top of the protocol pane for
 * jobs in `4-auto-review`, `5-human-review`, `6-completed`, or `7-archive`.
 * Surfaces the three signals the user should not be able to miss:
 *
 *  - whether the task carries a platform-owned commit stamp,
 *  - whether the working tree is dirty (and therefore "accepted task work
 *    is sitting uncommitted"),
 *  - whether stamped commits are still ahead of the upstream (push pending).
 *
 * When accepted task work appears uncommitted the strip also shows a
 * "Commit accepted task evidence" action that runs through the platform's
 * commit-message path, stamps `JobInfo.Commit`, and writes a
 * `[decision]` orchestrator-chat entry so the action is visible in the
 * activity log.
 */
@Component({
  selector: 'app-hygiene-strip',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    @if (visibleForState()) {
      <section class="hygiene-strip"
               [attr.data-testid]="'hygiene-strip'"
               [class.hygiene-strip--dirty]="hygiene()?.job?.acceptedTaskUncommitted"
               [class.hygiene-strip--unpushed]="hygiene()?.job?.commitUnpushed">
        <div class="hygiene-strip__row">
          <span class="hygiene-strip__title">Repository hygiene</span>

          <span class="hygiene-strip__chip"
                [class.hygiene-strip__chip--ok]="hygiene()?.job?.jobInfoCommitPresent"
                [class.hygiene-strip__chip--warn]="hygiene() && !hygiene()?.job?.jobInfoCommitPresent"
                [attr.data-testid]="'hygiene-commit'">
            @if (hygiene()?.job?.jobInfoCommitPresent) {
              <span aria-hidden="true">✓</span>
              <span>Task committed</span>
              @if (hygiene()?.job?.stampedCommitSha) {
                <code class="hygiene-strip__sha">{{ shortSha(hygiene()!.job!.stampedCommitSha) }}</code>
              }
            } @else {
              <span aria-hidden="true">○</span>
              <span>No task commit recorded</span>
            }
          </span>

          @if (hygiene(); as h) {
            <span class="hygiene-strip__chip"
                  [class.hygiene-strip__chip--ok]="!h.isDirty"
                  [class.hygiene-strip__chip--warn]="h.isDirty"
                  [attr.data-testid]="'hygiene-tree'">
              @if (h.isDirty) {
                <span aria-hidden="true">⚠</span>
                <span>Working tree dirty</span>
                <span class="hygiene-strip__detail">
                  ({{ h.stagedCount }} staged · {{ h.unstagedCount }} unstaged · {{ h.untrackedCount }} untracked)
                </span>
              } @else {
                <span aria-hidden="true">✓</span>
                <span>Working tree clean</span>
              }
            </span>

            <span class="hygiene-strip__chip"
                  [class.hygiene-strip__chip--ok]="h.hasUpstream && h.ahead === 0"
                  [class.hygiene-strip__chip--warn]="h.hasUpstream && h.ahead > 0"
                  [class.hygiene-strip__chip--neutral]="!h.hasUpstream"
                  [attr.data-testid]="'hygiene-push'">
              @if (!h.hasUpstream) {
                <span aria-hidden="true">·</span>
                <span>No upstream configured</span>
              } @else if (h.ahead > 0) {
                <span aria-hidden="true">↑</span>
                <span>{{ h.ahead }} commit{{ h.ahead === 1 ? '' : 's' }} ahead — push pending</span>
              } @else {
                <span aria-hidden="true">✓</span>
                <span>In sync with {{ h.upstream }}</span>
              }
            </span>

            @if (h.branch) {
              <span class="hygiene-strip__branch" [title]="'Current branch'">⎇ {{ h.branch }}</span>
            }
          } @else {
            <span class="hygiene-strip__chip hygiene-strip__chip--loading">Loading repository state…</span>
          }
        </div>

        @if (hygiene()?.job?.acceptedTaskUncommitted) {
          <div class="hygiene-strip__warning" data-testid="hygiene-warning-dirty-after-accept">
            <span class="hygiene-strip__warning-icon" aria-hidden="true">⚠</span>
            <div class="hygiene-strip__warning-body">
              <strong>Accepted task work is sitting uncommitted.</strong>
              The task moved to <code>{{ hygiene()!.job!.state }}</code> but the
              working tree still has changes that aren't recorded on this job.
              Commit them now so the evidence travels with the task.
            </div>
            <button type="button"
                    class="hygiene-strip__action"
                    data-testid="hygiene-commit-accepted"
                    [disabled]="committing()"
                    (click)="commitAccepted()">
              {{ committing() ? 'Committing…' : 'Commit accepted task evidence' }}
            </button>
          </div>
        }

        @if (hygiene()?.job?.commitUnpushed) {
          <div class="hygiene-strip__warning hygiene-strip__warning--unpushed"
               data-testid="hygiene-warning-unpushed">
            <span class="hygiene-strip__warning-icon" aria-hidden="true">↑</span>
            <div class="hygiene-strip__warning-body">
              <strong>Push pending.</strong>
              The recorded task commit hasn't reached
              <code>{{ hygiene()?.upstream }}</code> yet
              ({{ hygiene()?.ahead }} commit{{ hygiene()?.ahead === 1 ? '' : 's' }}
              ahead of upstream).
              Push from your local checkout when you're ready — the platform
              does not push automatically.
            </div>
          </div>
        }
      </section>
    }
  `,
  styleUrls: ['./hygiene-strip.component.scss']
})
export class HygieneStripComponent implements OnDestroy {
  readonly job = input.required<JobInfo>();

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

  private pollTimer: ReturnType<typeof setInterval> | null = null;
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
    this.pollTimer = setInterval(() => this.refreshOnce(j), 15_000);
  }

  private refreshOnce(j: JobInfo): void {
    this.hygieneSvc.fetchForJob(j.id, j.watchPath).subscribe({
      next: (s) => this.hygiene.set(s),
      error: () => { /* keep last snapshot */ }
    });
  }

  private stopPolling(): void {
    if (this.pollTimer) {
      clearInterval(this.pollTimer);
      this.pollTimer = null;
    }
  }
}
