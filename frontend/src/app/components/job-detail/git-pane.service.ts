import { Injectable, OnDestroy, computed, signal } from '@angular/core';
import { GitStatus, JobCommitDetail, JobInfo } from '../../models/job.model';
import { JobService } from '../../services/job.service';
import { ErrorDialogService } from '../../services/error-dialog.service';

/**
 * Owns the Git pane state and API calls for a single job-detail
 * instance: working-tree status, the currently-selected diff path and
 * its body, the commit-message draft, and the generate/commit progress
 * flags.
 *
 * Provided locally on JobDetailComponent. The component supplies the
 * current `JobInfo` (via setJob) and the service drives all backend
 * traffic + signals from there.
 */
@Injectable()
export class GitPaneService implements OnDestroy {
  readonly status = signal<GitStatus | null>(null);
  readonly loading = signal(false);
  readonly selectedDiffPath = signal<string | null>(null);
  readonly diffText = signal<string>('');
  readonly commitMessage = signal('');
  readonly committing = signal(false);
  readonly generatingMsg = signal(false);

  // Commit-history view: when the task has an auto-commit recorded, the
  // pane switches from "live working tree" to "what this task changed".
  // That data survives future work in the repo and is what the user wants
  // to see when reviewing a finished task.
  readonly commitDetail = signal<JobCommitDetail | null>(null);
  readonly viewMode = computed<'commit' | 'worktree'>(() =>
    this.commitDetail()?.commit ? 'commit' : 'worktree'
  );

  private currentJob: JobInfo | null = null;
  private refreshTimer: ReturnType<typeof setInterval> | null = null;

  constructor(
    private jobService: JobService,
    private errorDialog: ErrorDialogService
  ) {}

  /** Start polling git status every `intervalMs` ms. No-op if already running. */
  startAutoRefresh(intervalMs = 5000): void {
    if (this.refreshTimer) return;
    this.refreshTimer = setInterval(() => {
      // In commit mode the displayed snapshot is historical — polling the
      // working tree would just churn for nothing.
      if (this.viewMode() === 'commit') return;
      if (!this.committing() && !this.generatingMsg()) {
        this.refresh();
      }
    }, intervalMs);
  }

  /** Stop the auto-refresh polling loop. */
  stopAutoRefresh(): void {
    if (this.refreshTimer) {
      clearInterval(this.refreshTimer);
      this.refreshTimer = null;
    }
  }

  ngOnDestroy(): void {
    this.stopAutoRefresh();
  }

  /**
   * Tell the service which job is currently displayed. Resets the pane
   * state when the job actually changes; same-job calls are no-ops so
   * we don't blow away in-flight selections.
   */
  setJob(info: JobInfo | null | undefined): void {
    const sameJob = this.currentJob && info
      && this.currentJob.id === info.id
      && this.currentJob.watchPath === info.watchPath;
    if (sameJob) {
      const hadCommit = !!this.currentJob!.commit;
      this.currentJob = info!;
      // The auto-commit lands on the progress→review transition, so a
      // refresh of the same job can flip from "no commit" to "has commit".
      // Load the snapshot lazily when that happens.
      if (!hadCommit && info!.commit) this.loadCommitDetail();
      return;
    }
    this.currentJob = info ?? null;
    this.status.set(null);
    this.commitDetail.set(null);
    this.selectedDiffPath.set(null);
    this.diffText.set('');
    this.commitMessage.set('');
    this.loading.set(false);
    this.committing.set(false);
    this.generatingMsg.set(false);
    if (info?.commit) this.loadCommitDetail();
  }

  /**
   * Load the recorded-commit snapshot for the current job. Tasks that have
   * been auto-committed on progress→review carry a JobCommitInfo on
   * job.json — the backend re-derives the file list from `git show` so the
   * pane stays accurate even after history rewrites.
   */
  loadCommitDetail(): void {
    const info = this.currentJob;
    if (!info) return;
    this.jobService.getJobCommit(info.id, info.watchPath).subscribe({
      next: (detail) => {
        this.commitDetail.set(detail);
        // Default-select the first changed file so the diff is visible at a
        // glance — matches the user's intent of "show me the changes".
        const first = detail?.files?.[0]?.path ?? null;
        if (first) this.selectDiffPath(first);
      },
      error: () => this.commitDetail.set(null)
    });
  }

  refresh(): void {
    const info = this.currentJob;
    if (!info) return;
    this.loading.set(true);
    this.jobService.getGitStatus(info.id, info.watchPath).subscribe({
      next: (status) => {
        this.status.set(status);
        this.loading.set(false);
        // If a previously selected file is no longer in the change set,
        // clear the diff so we don't keep stale text on screen.
        const selected = this.selectedDiffPath();
        if (selected && !status.files.some(f => f.path === selected)) {
          this.selectedDiffPath.set(null);
          this.diffText.set('');
        }
      },
      error: (err) => {
        this.loading.set(false);
        this.errorDialog.show(err, { title: 'Git status failed', source: `Task ${info.id}` });
      }
    });
  }

  selectDiffPath(path: string): void {
    if (this.selectedDiffPath() === path) {
      this.selectedDiffPath.set(null);
      this.diffText.set('');
      return;
    }
    const info = this.currentJob;
    if (!info) return;
    this.selectedDiffPath.set(path);
    this.diffText.set('');
    // In commit mode the diff comes from `git show <sha> -- <path>` so we
    // see the historical change, not whatever the working tree looks like
    // right now.
    const stream$ = this.viewMode() === 'commit'
      ? this.jobService.getJobCommitDiff(info.id, path, info.watchPath)
      : this.jobService.getGitDiff(info.id, path, info.watchPath);
    stream$.subscribe({
      next: (text: unknown) => this.diffText.set(typeof text === 'string' ? text : ''),
      error: () => this.diffText.set('(failed to load diff)')
    });
  }

  generateCommitMessage(): void {
    const info = this.currentJob;
    if (!info) return;
    this.generatingMsg.set(true);
    this.jobService.generateCommitMessage(info.id, info.watchPath).subscribe({
      next: (res) => {
        this.generatingMsg.set(false);
        if (res?.message) this.commitMessage.set(res.message);
      },
      error: (err) => {
        this.generatingMsg.set(false);
        this.errorDialog.show(err, { title: 'Generate commit message failed', source: `Task ${info.id}` });
      }
    });
  }

  commit(): void {
    const info = this.currentJob;
    const msg = this.commitMessage().trim();
    if (!info || !msg) return;
    this.committing.set(true);
    this.jobService.commitJob(info.id, msg, info.watchPath).subscribe({
      next: () => {
        this.committing.set(false);
        this.commitMessage.set('');
        this.refresh();
      },
      error: (err) => {
        this.committing.set(false);
        this.errorDialog.show(err, { title: 'Commit failed', source: `Task ${info.id}` });
      }
    });
  }

  openInVsCode(): void {
    const info = this.currentJob;
    if (!info) return;
    this.jobService.openInVsCode(info.id, info.watchPath).subscribe({
      error: (err) => this.errorDialog.show(err, { title: 'Open in VS Code failed', source: `Task ${info.id}` })
    });
  }
}
