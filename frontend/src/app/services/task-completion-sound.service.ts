import { Injectable, effect, inject } from '@angular/core';
import { JobService } from './task.service';
import { JobInfo } from '../models/task.model';

/**
 * Plays a short beep when a job leaves the `progress` lane for `review` or
 * `completed` since the last polled snapshot. Browser-only; uses Web Audio
 * API so no asset file is needed and the app keeps shipping as a single
 * static bundle.
 *
 * Mounted by AppComponent so the watcher starts on boot.
 *
 * Skip rules (per user request):
 * - Title that starts with `e2e` (case-insensitive) does not play a sound;
 *   E2E spec runs flood the queue and would create noise.
 * - Skip the very first poll after mount; the initial snapshot is not a
 *   "transition", just an observation. Otherwise refreshing the app while
 *   a job sits in 4-auto-review would beep on every reload.
 *
 * Mute via `localStorage.setItem('atp.muteCompletionSound', '1')`.
 */
@Injectable({ providedIn: 'root' })
export class JobCompletionSoundService {
  private readonly jobService = inject(JobService);

  private previousProgressIds = new Set<string>();
  private firstSnapshotConsumed = false;
  private audioContext: AudioContext | null = null;

  constructor() {
    effect(() => {
      const grouped = this.jobService.grouped();
      const inProgress = new Set((grouped.progress ?? []).map((j: JobInfo) => j.id));
      if (!this.firstSnapshotConsumed) {
        this.previousProgressIds = inProgress;
        this.firstSnapshotConsumed = true;
        return;
      }

      const autoReview = new Set((grouped.autoReview ?? grouped.review ?? []).map((j: JobInfo) => j.id));
      const humanReview = new Set((grouped.humanReview ?? []).map((j: JobInfo) => j.id));
      const completed = new Set((grouped.completed ?? []).map((j: JobInfo) => j.id));
      const completedJobs: JobInfo[] = [];
      for (const id of this.previousProgressIds) {
        if (inProgress.has(id)) continue;
        if (autoReview.has(id) || humanReview.has(id) || completed.has(id)) {
          const j = this.findJob(grouped, id);
          if (j) completedJobs.push(j);
        }
      }
      this.previousProgressIds = inProgress;

      for (const job of completedJobs) {
        if (this.shouldSkip(job)) continue;
        this.beep();
      }
    });
  }

  private shouldSkip(job: JobInfo): boolean {
    if (typeof window !== 'undefined' && window.localStorage?.getItem('atp.muteCompletionSound') === '1') return true;
    const title = (job.title || '').trim().toLowerCase();
    if (title.startsWith('e2e')) return true;
    if (title.startsWith('@billable')) return true;
    return false;
  }

  private findJob(
    grouped: { autoReview?: JobInfo[]; humanReview?: JobInfo[]; review?: JobInfo[]; completed?: JobInfo[] },
    id: string,
  ): JobInfo | undefined {
    return (grouped.autoReview ?? grouped.review ?? []).find(j => j.id === id)
      ?? (grouped.humanReview ?? []).find(j => j.id === id)
      ?? (grouped.completed ?? []).find(j => j.id === id);
  }

  private beep(): void {
    if (typeof window === 'undefined') return;
    try {
      this.audioContext ??= new (window.AudioContext || (window as unknown as { webkitAudioContext: typeof AudioContext }).webkitAudioContext)();
      const ctx = this.audioContext;
      if (ctx.state === 'suspended') ctx.resume().catch(() => undefined);

      const now = ctx.currentTime;
      // Two-note chime: B5 (988 Hz) -> E6 (1318 Hz). Short, soft, distinct.
      this.tone(ctx, 988, now, 0.12, 0.08);
      this.tone(ctx, 1318, now + 0.12, 0.18, 0.10);
    } catch {
      // Audio is best-effort; never throw out of the polling effect.
    }
  }

  private tone(ctx: AudioContext, freq: number, startAt: number, duration: number, peakGain: number): void {
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = 'sine';
    osc.frequency.value = freq;
    gain.gain.setValueAtTime(0, startAt);
    gain.gain.linearRampToValueAtTime(peakGain, startAt + 0.015);
    gain.gain.exponentialRampToValueAtTime(0.0001, startAt + duration);
    osc.connect(gain).connect(ctx.destination);
    osc.start(startAt);
    osc.stop(startAt + duration + 0.05);
  }
}
