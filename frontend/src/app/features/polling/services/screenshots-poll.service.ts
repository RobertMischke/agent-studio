import { Injectable, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import type { JobScreenshot, JobScreenshotsResponse } from '../../../features/screenshots';
import { JobService } from '../../../services/task.service';
import { JobBackgroundPoller } from './task-background-poller';

/**
 * Polls the per-job screenshot listing (`/api/jobs/{id}/screenshots`)
 * on a slow cadence (10 s). Screenshots only appear when a Playwright
 * spec or an agent script writes a new file to `<job>/results/`, so a
 * shorter cadence would burn requests for nothing; the file system is
 * cheap on the backend, so 10 s is a comfortable trade.
 */
@Injectable()
export class ScreenshotsPollService extends JobBackgroundPoller<JobScreenshotsResponse | null> {
  private readonly jobService = inject(JobService);

  protected readonly intervalMs = 10_000;

  readonly screenshots = signal<JobScreenshot[]>([]);

  protected fetch(jobId: string, watchPath: string): Observable<JobScreenshotsResponse | null> {
    return this.jobService.getJobScreenshots(jobId, watchPath);
  }

  protected applyResponse(res: JobScreenshotsResponse | null): void {
    this.screenshots.set(res?.screenshots ?? []);
  }

  protected clearValue(): void {
    this.screenshots.set([]);
  }
}
