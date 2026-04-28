import { Injectable, OnDestroy, signal } from '@angular/core';
import { CliExecution, CliOutputLine } from '../../models/job.model';
import { JobService } from '../../services/job.service';

interface JobRef { id: string; watchPath: string; }

/**
 * Owns the live-CLI side of the job-detail view: the rolling output
 * buffer, the elapsed-time ticker, and the 2 s output-poll loop. The
 * component supplies the current job (via setJob) and the methods
 * below; the service drives all timers + signals from there.
 *
 * Provided locally on JobDetailComponent so each detail instance has
 * its own state and timers.
 */
@Injectable()
export class CliOutputPollService implements OnDestroy {
  readonly output = signal<CliOutputLine[]>([]);
  readonly isRunning = signal(false);
  readonly startedAt = signal<Date | null>(null);
  readonly elapsedTime = signal('');

  private elapsedTimer: ReturnType<typeof setInterval> | null = null;
  private pollGeneration = 0;
  private pollTimeout: ReturnType<typeof setTimeout> | null = null;
  private currentJob: JobRef | null = null;

  constructor(private jobService: JobService) {}

  setJob(job: JobRef | null): void {
    this.currentJob = job;
  }

  /** Reset all per-job state — call when switching to a different job. */
  resetForJobSwitch(): void {
    this.output.set([]);
    this.isRunning.set(false);
    this.startedAt.set(null);
    this.elapsedTime.set('0s');
    this.pollGeneration += 1;
    this.clearPollTimer();
    this.clearElapsedTimer();
  }

  /** Mirror an incoming CliExecution snapshot into running/started state. */
  applyExecution(execution: CliExecution | null): void {
    if (!execution) return;
    if (execution.status === 'running') {
      this.isRunning.set(true);
      this.startedAt.set(new Date(execution.startedAt));
      if (!this.elapsedTimer) this.startElapsedTimer();
      return;
    }
    this.isRunning.set(false);
    this.clearElapsedTimer();
  }

  /** Begin a fresh run with a known startedAt — clears the buffer first. */
  beginRun(startedAt: Date): void {
    this.isRunning.set(true);
    this.startedAt.set(startedAt);
    this.output.set([]);
    this.startElapsedTimer();
    this.startPolling();
  }

  /** Halt timers and polling without resetting buffers — used by Stop. */
  stop(): void {
    this.isRunning.set(false);
    this.clearPollTimer();
    this.clearElapsedTimer();
  }

  /**
   * Start polling the output endpoint if not already running. Public
   * because the detail-effect calls it whenever the loaded job is
   * already in `running` state.
   */
  startPolling(): void {
    if (this.pollTimeout) return;
    const generation = this.pollGeneration;
    const poll = () => {
      const job = this.currentJob;
      if (!this.isRunning() || generation !== this.pollGeneration || !job) return;
      this.jobService.getJobOutput(job.id, job.watchPath).subscribe({
        next: (output) => {
          if (generation !== this.pollGeneration) return;
          this.output.set(output);
          this.pollTimeout = setTimeout(poll, 2000);
        },
        error: () => {
          this.pollTimeout = setTimeout(poll, 5000);
        }
      });
    };
    this.pollTimeout = setTimeout(poll, 1000);
  }

  /** Whether the output buffer is currently being polled. */
  isPolling(): boolean { return this.pollTimeout !== null; }

  /** Hydrate the buffer from a prior run's logs (called by detail-effect). */
  hydrateOutput(output: CliOutputLine[], execStartedAt?: string | null): void {
    if (output.length === 0) return;
    this.output.set(output);
    if (!this.startedAt() && execStartedAt) {
      this.startedAt.set(new Date(execStartedAt));
    }
    if (!this.elapsedTimer && this.isRunning()) {
      this.startElapsedTimer();
    }
  }

  private startElapsedTimer(): void {
    this.clearElapsedTimer();
    this.updateElapsed();
    this.elapsedTimer = setInterval(() => this.updateElapsed(), 1000);
  }

  private updateElapsed(): void {
    const start = this.startedAt();
    if (!start) { this.elapsedTime.set('0s'); return; }
    const secs = Math.floor((Date.now() - start.getTime()) / 1000);
    if (secs < 60) this.elapsedTime.set(`${secs}s`);
    else if (secs < 3600) this.elapsedTime.set(`${Math.floor(secs / 60)}m ${secs % 60}s`);
    else this.elapsedTime.set(`${Math.floor(secs / 3600)}h ${Math.floor((secs % 3600) / 60)}m`);
  }

  private clearPollTimer(): void {
    if (this.pollTimeout) {
      clearTimeout(this.pollTimeout);
      this.pollTimeout = null;
    }
  }

  private clearElapsedTimer(): void {
    if (this.elapsedTimer) {
      clearInterval(this.elapsedTimer);
      this.elapsedTimer = null;
    }
  }

  ngOnDestroy(): void {
    this.isRunning.set(false);
    this.clearPollTimer();
    this.clearElapsedTimer();
  }
}
