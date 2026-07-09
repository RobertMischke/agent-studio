import { Injectable, inject, signal } from '@angular/core';
import { Observable } from 'rxjs';
import { TaskService } from '../../../services/task.service';
import type { TaskArtifact, TaskArtifactsResponse } from '../../../models/task.model';
import { TaskBackgroundPoller } from '../../polling/services/task-background-poller';

/**
 * Per-detail Files-tab manifest. Owns the list of user-relevant `.md`
 * artifacts in the job root (prompt + aspect verdicts + code-review +
 * notes) that the Files tab renders and the Files-tab count badge sums.
 * Internal machinery (`logs/`, `run-context/`, `*.json` such as
 * `lifecycle.json` / `pipeline-execution.json`) never appears here — the
 * backend `/artifacts` endpoint only enumerates top-level `*.md` files
 * (and drops `status.md`), so those are out of scope by construction.
 *
 * Polls on a slow cadence so the count stays live while a run generates
 * fresh aspect / code-review files, instead of freezing at the value
 * captured when the task was first opened. Mirrors
 * {@link ScreenshotsPollService}; provided locally on
 * `TaskDetailComponent` (no global state).
 */
@Injectable()
export class TaskArtifactsService extends TaskBackgroundPoller<TaskArtifactsResponse | null> {
  private readonly jobs = inject(TaskService);

  // Files change only when the runner writes a new `.md` into the job
  // root, so a 10 s cadence keeps the count fresh without hammering the
  // backend — same trade-off as the sibling screenshots poll.
  protected readonly intervalMs = 10_000;

  readonly artifacts = signal<TaskArtifact[]>([]);

  protected fetch(jobId: string, watchPath: string): Observable<TaskArtifactsResponse | null> {
    return this.jobs.listJobArtifacts(jobId, watchPath);
  }

  protected applyResponse(res: TaskArtifactsResponse | null): void {
    this.artifacts.set(res?.files ?? []);
  }

  protected clearValue(): void {
    this.artifacts.set([]);
  }
}
