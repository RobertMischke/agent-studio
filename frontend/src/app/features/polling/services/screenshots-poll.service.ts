import { Injectable, OnDestroy, signal } from '@angular/core';
import type { JobInfo } from '../../../models/job.model';
import type { JobScreenshot } from '../../../features/screenshots';
import { JobService } from '../../../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';

/**
 * Polls the per-job screenshot listing (`/api/jobs/{id}/screenshots`)
 * on a slow cadence (10 s). Screenshots only appear when a Playwright
 * spec or an agent script writes a new file to `<job>/results/`, so a
 * shorter cadence would burn requests for nothing; the file system is
 * cheap on the backend, so 10 s is a comfortable trade.
 *
 * Refreshes immediately when the job context changes so the strip
 * never lags behind a job switch.
 */
@Injectable()
export class ScreenshotsPollService implements OnDestroy {
  readonly screenshots = signal<JobScreenshot[]>([]);

  private timer: VisibleIntervalHandle | null = null;
  private currentJob: { id: string; watchPath: string } | null = null;
  private currentKey = '';

  constructor(private jobService: JobService) {}

  syncTo(info: JobInfo | null | undefined): void {
    const key = info ? `${info.watchPath}::${info.id}` : '';
    if (key === this.currentKey) return;
    this.currentKey = key;
    this.stop();
    if (!info) {
      this.screenshots.set([]);
      return;
    }
    this.currentJob = { id: info.id, watchPath: info.watchPath };
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(), 10_000);
  }

  refresh(): void {
    const job = this.currentJob;
    if (!job) {
      this.screenshots.set([]);
      return;
    }
    this.jobService.getJobScreenshots(job.id, job.watchPath).subscribe({
      next: (res) => this.screenshots.set(res?.screenshots ?? []),
      error: () => { /* non-fatal: keep previous snapshot */ }
    });
  }

  stop(): void {
    if (this.timer) {
      clearVisibleInterval(this.timer);
      this.timer = null;
    }
  }

  ngOnDestroy(): void {
    this.stop();
  }
}
