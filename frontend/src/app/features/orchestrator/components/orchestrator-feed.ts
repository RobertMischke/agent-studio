import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import type { OrchestratorLogEntry } from '../../../features/orchestrator';
import { JobService } from '../../../services/job.service';
import { TokenSummaryBlockComponent } from '../../tokens/components/token-summary-block';
import { GlobalOrchestratorCardComponent } from './global-orchestrator-card';

/**
 * Per-project orchestrator log feed. Reads
 * `/api/runner/{projectName}/orchestrator-log` on init and every 10s
 * while mounted. Renders a chronological list of entries: decisions,
 * actions (queued follow-ups, watchdog kills), observations,
 * interventions. The entry shape carries enough metadata for future
 * "override this decision" affordances (kept as a TODO note in the UI
 * but not wired today).
 *
 * Today's entries are written by the runner / watchdog. Phase D will
 * add an orchestrator-as-CLI process that writes its own reasoning
 * here with the same shape, so the feed stays one timeline.
 */
@Component({
  selector: 'app-orchestrator-feed',
  standalone: true,
  imports: [FormsModule, TokenSummaryBlockComponent, GlobalOrchestratorCardComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-feed.html',
  styleUrl: './orchestrator-feed.scss'
})
export class OrchestratorFeedComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly jobService = inject(JobService);
  readonly entries = signal<OrchestratorLogEntry[]>([]);
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  /** Timestamp of the entry currently being overridden (one at a time). */
  readonly overridingTs = signal<string | null>(null);
  /** Submit-in-flight flag so the user cannot double-send. */
  readonly submittingOverride = signal(false);
  /** Two-way bound textarea draft. Cleared after each submit / cancel. */
  overrideDraft = '';
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  /** UI shows newest entries first; the on-disk log is oldest first. */
  readonly reversed = computed(() => [...this.entries()].reverse());

  ngOnInit(): void {
    this.refresh();
    this.pollTimer = setInterval(() => this.refresh(true), 10_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  refresh(silent = false): void {
    if (!silent) this.loading.set(true);
    this.jobService.getOrchestratorLog(this.projectName()).subscribe({
      next: (resp) => {
        this.entries.set(resp.entries ?? []);
        this.error.set(null);
        if (!silent) this.loading.set(false);
      },
      error: (err) => {
        const message = err?.error?.error || err?.message || 'Failed to load orchestrator log';
        this.error.set(message);
        if (!silent) this.loading.set(false);
      }
    });
  }

  kindLabel(kind: string): string {
    switch (kind) {
      case 'decision': return 'Decision';
      case 'action': return 'Action';
      case 'observation': return 'Observation';
      case 'intervention': return 'Intervention';
      default: return kind;
    }
  }

  formatTime(iso: string): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch {
      return iso;
    }
  }

  startOverride(entry: OrchestratorLogEntry): void {
    this.overridingTs.set(entry.ts);
    this.overrideDraft = '';
  }

  cancelOverride(): void {
    this.overridingTs.set(null);
    this.overrideDraft = '';
  }

  submitOverride(entry: OrchestratorLogEntry): void {
    const direction = (this.overrideDraft ?? '').trim();
    if (!direction || !entry.jobId) return;
    this.submittingOverride.set(true);
    this.jobService.overrideOrchestratorEntry(this.projectName(), {
      originalTs: entry.ts,
      jobId: entry.jobId,
      newDirection: direction
    }).subscribe({
      next: () => {
        this.submittingOverride.set(false);
        this.overridingTs.set(null);
        this.overrideDraft = '';
        // Refresh so the new intervention entry shows up.
        this.refresh(true);
      },
      error: (err) => {
        this.submittingOverride.set(false);
        const message = err?.error?.error || err?.message || 'Override failed';
        this.error.set(message);
      }
    });
  }

  tokenTooltip(tu: NonNullable<OrchestratorLogEntry['tokenUsage']>): string {
    return [
      `Model: ${tu.model || '?'}`,
      `Input: ${tu.inputTokens.toLocaleString()} tokens`,
      `Output: ${tu.outputTokens.toLocaleString()} tokens`,
      `Cache read: ${tu.cacheReadTokens.toLocaleString()} tokens`,
      `Cache creation: ${tu.cacheCreationTokens.toLocaleString()} tokens`
    ].join('\n');
  }
}
