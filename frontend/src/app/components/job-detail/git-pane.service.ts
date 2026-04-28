import { Injectable, signal } from '@angular/core';
import { GitStatus, JobInfo } from '../../models/job.model';
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
export class GitPaneService {
  readonly status = signal<GitStatus | null>(null);
  readonly loading = signal(false);
  readonly selectedDiffPath = signal<string | null>(null);
  readonly diffText = signal<string>('');
  readonly commitMessage = signal('');
  readonly committing = signal(false);
  readonly generatingMsg = signal(false);

  private currentJob: JobInfo | null = null;

  constructor(
    private jobService: JobService,
    private errorDialog: ErrorDialogService
  ) {}

  /**
   * Tell the service which job is currently displayed. Resets the pane
   * state when the job actually changes; same-job calls are no-ops so
   * we don't blow away in-flight selections.
   */
  setJob(info: JobInfo | null | undefined): void {
    if (this.currentJob && info && this.currentJob.id === info.id && this.currentJob.watchPath === info.watchPath) {
      this.currentJob = info;
      return;
    }
    this.currentJob = info ?? null;
    this.status.set(null);
    this.selectedDiffPath.set(null);
    this.diffText.set('');
    this.commitMessage.set('');
    this.loading.set(false);
    this.committing.set(false);
    this.generatingMsg.set(false);
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
    this.jobService.getGitDiff(info.id, path, info.watchPath).subscribe({
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
