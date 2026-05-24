import { Injectable, inject, signal } from '@angular/core';
import { JobService } from '../../../services/job.service';
import type { JobArtifact, JobInfo } from '../../../models/job.model';

/**
 * Per-detail Files-tab manifest. Refreshes when the open job changes
 * and exposes a manual {@link reload} call so a successful file save
 * can pick up the new size + mtime without a page reload. Provided
 * locally on `JobDetailComponent` (no global state).
 */
@Injectable()
export class JobArtifactsService {
  private readonly jobs = inject(JobService);
  private currentKey: string | null = null;

  readonly artifacts = signal<JobArtifact[]>([]);

  /** Called from a detail-component effect whenever the open job changes. */
  syncTo(info: JobInfo | null): void {
    if (!info) {
      this.currentKey = null;
      this.artifacts.set([]);
      return;
    }
    if (this.currentKey === info.jobKey) return;
    this.currentKey = info.jobKey;
    this.fetch(info);
  }

  /** Forces a re-fetch against the currently tracked job. No-op when unbound. */
  reload(info: JobInfo | null): void {
    if (!info) return;
    this.fetch(info);
  }

  private fetch(info: JobInfo): void {
    this.jobs.listJobArtifacts(info.id, info.watchPath).subscribe({
      next: (resp) => this.artifacts.set(resp?.files ?? []),
      error: () => this.artifacts.set([]),
    });
  }
}
