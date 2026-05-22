import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { JobService } from '../../../../services/job.service';
import type { JobInfo } from '../../../../models/job.model';

/**
 * Full-screen "Diff" tab. Resolves the owning job for a commit SHA by
 * walking the live job index — when found, surfaces the project +
 * commit metadata and an "Open task" CTA so the user can jump to the
 * existing in-task diff pane. The inline diff renderer is a follow-up;
 * this view is the shell for it and keeps the tab kind productive
 * (commit SHA + file count + author) instead of dead.
 *
 * The commit message renders collapsed-by-default: only the first line
 * (the subject) is visible at rest, dimmed expander caret on the right.
 * Click toggles full body. Operator feedback 2026-05-22 — a 5-line
 * message block was crowding the meta-grid + diff renderer.
 */
@Component({
  selector: 'app-studio-diff-view',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './diff-tab-view.component.html',
  styleUrl: './diff-tab-view.component.scss',
})
export class StudioDiffViewComponent {
  private readonly jobService = inject(JobService);

  readonly commitSha = input.required<string>();

  /** Commit-message expander state. Collapsed = first line + ellipsis. */
  readonly messageExpanded = signal(false);

  toggleMessage(): void {
    this.messageExpanded.update((v) => !v);
  }

  /** True when the message body has more than one non-empty line. */
  hasMoreLines(message: string | undefined): boolean {
    if (!message) return false;
    const lines = message.split(/\r?\n/);
    if (lines.length <= 1) return false;
    return lines.slice(1).some((l) => l.trim().length > 0);
  }

  readonly owner = computed<{ job: JobInfo; commit: { sha: string; message?: string; filesChanged?: number; at?: string } } | null>(() => {
    const sha = this.commitSha();
    const short = sha.slice(0, 7);
    const jobs = this.jobService.jobs();
    for (const job of jobs) {
      const commits = job.commits ?? [];
      for (const c of commits) {
        if (c.sha === sha || c.sha?.startsWith(short)) {
          return {
            job,
            commit: {
              sha: c.sha ?? sha,
              message: c.message,
              filesChanged: c.filesChanged ?? undefined,
              at: c.at,
            },
          };
        }
      }
      if (job.commit && (job.commit.sha === sha || job.commit.sha?.startsWith(short))) {
        return {
          job,
          commit: {
            sha: job.commit.sha ?? sha,
            message: job.commit.message,
            filesChanged: job.commit.filesChanged ?? undefined,
            at: job.commit.at,
          },
        };
      }
    }
    return null;
  });
}
