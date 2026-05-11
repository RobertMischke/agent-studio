import { Injectable, computed, signal } from '@angular/core';
import { JobInfo } from '../../../models/job.model';

/**
 * Lane pager snapshot: the captured iteration the detail header walks
 * with its Prev / N of M / Next controls. The snapshot is intentionally
 * stable across status changes: if the user moves the current job out
 * of the captured lane mid-iteration, the snapshot keeps the moved job
 * at its original index so a subsequent "Next" click advances to the
 * job that was after it at capture time, not to whatever live ordering
 * shows now.
 *
 * Persistence: stored in sessionStorage under a versioned key so a
 * page reload restores the iteration. The snapshot is tab-scoped on
 * purpose; opening a second tab on the same workspace starts its own
 * iteration.
 */
export interface LanePagerEntry {
  jobKey: string;
  id: string;
  watchPath: string;
  title: string | null;
}

export interface LanePagerSnapshot {
  lane: string;
  jobs: LanePagerEntry[];
  index: number;
  capturedAt: number;
}

const STORAGE_KEY = 'app:lanePager:v1';

const LANE_LABELS: Record<string, string> = {
  '0-backlog':              'Backlog',
  '1-preparation':          'Preparation',
  '1a-orchestrator-prep':   'Orchestrator Prep',
  '1b-needs-human-review':  'Needs Clarification',
  '2-ready':                'Ready',
  '3-progress':             'In Progress',
  '3a-failed-pickup':       'Failed Pickup',
  '4-auto-review':          'Auto Review',
  '5-human-review':         'Human Review',
  '6-completed':            'Completed',
  '7-archive':              'Archive',
};

@Injectable({ providedIn: 'root' })
export class LanePagerService {
  /**
   * The captured iteration. `null` when the detail view wasn't opened
   * from a lane (e.g. pasted URL with no prior snapshot) or after the
   * snapshot was cleared.
   */
  readonly snapshot = signal<LanePagerSnapshot | null>(this.loadFromStorage());

  /** 1-based human-readable position; 0 when no snapshot. */
  readonly position = computed(() => {
    const s = this.snapshot();
    return s ? s.index + 1 : 0;
  });

  readonly total = computed(() => this.snapshot()?.jobs.length ?? 0);

  readonly canPrev = computed(() => {
    const s = this.snapshot();
    return !!s && s.index > 0;
  });

  readonly canNext = computed(() => {
    const s = this.snapshot();
    return !!s && s.index < s.jobs.length - 1;
  });

  readonly laneLabel = computed(() => {
    const s = this.snapshot();
    if (!s) return '';
    return LANE_LABELS[s.lane] ?? s.lane;
  });

  /**
   * Capture a fresh snapshot of `lane`'s current peers, anchoring the
   * iteration index at `currentJobKey`. Pass an empty `peers` list (or
   * a job not in the lane) to clear the snapshot.
   */
  capture(lane: string, peers: readonly JobInfo[], currentJobKey: string): void {
    if (peers.length === 0) {
      this.clear();
      return;
    }
    const jobs: LanePagerEntry[] = peers.map(p => ({
      jobKey: p.jobKey,
      id: p.id,
      watchPath: p.watchPath,
      title: p.title ?? null,
    }));
    const idx = jobs.findIndex(j => j.jobKey === currentJobKey);
    if (idx < 0) {
      this.clear();
      return;
    }
    const next: LanePagerSnapshot = {
      lane,
      jobs,
      index: idx,
      capturedAt: Date.now(),
    };
    this.snapshot.set(next);
    this.persist(next);
  }

  /**
   * Advance the snapshot index by `delta` (`+1` for Next, `-1` for
   * Prev). Returns the entry at the new index, or `null` if the move
   * would step past a boundary or no snapshot exists.
   */
  step(delta: -1 | 1): LanePagerEntry | null {
    const s = this.snapshot();
    if (!s) return null;
    const nextIdx = s.index + delta;
    if (nextIdx < 0 || nextIdx >= s.jobs.length) return null;
    const updated: LanePagerSnapshot = { ...s, index: nextIdx };
    this.snapshot.set(updated);
    this.persist(updated);
    return s.jobs[nextIdx];
  }

  /**
   * Reconcile the snapshot's index to a specific job key. Used when
   * selection lands on a snapshot member through a path other than
   * pager step (e.g. URL restore on reload). No-op when the job is
   * not part of the current snapshot.
   */
  reanchorTo(jobKey: string): void {
    const s = this.snapshot();
    if (!s) return;
    const idx = s.jobs.findIndex(j => j.jobKey === jobKey);
    if (idx < 0 || idx === s.index) return;
    const updated: LanePagerSnapshot = { ...s, index: idx };
    this.snapshot.set(updated);
    this.persist(updated);
  }

  clear(): void {
    this.snapshot.set(null);
    if (typeof sessionStorage !== 'undefined') {
      try { sessionStorage.removeItem(STORAGE_KEY); } catch { /* ignore quota errors */ }
    }
  }

  private persist(s: LanePagerSnapshot): void {
    if (typeof sessionStorage === 'undefined') return;
    try {
      sessionStorage.setItem(STORAGE_KEY, JSON.stringify(s));
    } catch {
      /* sessionStorage may be unavailable in private mode; iteration still works in-memory */
    }
  }

  private loadFromStorage(): LanePagerSnapshot | null {
    if (typeof sessionStorage === 'undefined') return null;
    try {
      const raw = sessionStorage.getItem(STORAGE_KEY);
      if (!raw) return null;
      const parsed = JSON.parse(raw) as LanePagerSnapshot;
      if (
        !parsed ||
        typeof parsed.lane !== 'string' ||
        !Array.isArray(parsed.jobs) ||
        typeof parsed.index !== 'number'
      ) return null;
      return parsed;
    } catch {
      return null;
    }
  }
}
