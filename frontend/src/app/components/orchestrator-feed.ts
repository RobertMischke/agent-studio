import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { OrchestratorLogEntry } from '../models/job.model';
import { JobService } from '../services/job.service';
import { TokenSummaryBlockComponent } from './token-summary-block';

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
  imports: [FormsModule, TokenSummaryBlockComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="orch-feed" data-testid="orchestrator-feed">
      <header class="orch-feed__head">
        <h2 class="orch-feed__title">⚙ Orchestrator feed</h2>
        <span class="orch-feed__sub">{{ projectName() }} — {{ entries().length }} entries</span>
        <button class="orch-feed__refresh" (click)="refresh()" [disabled]="loading()" data-testid="orchestrator-refresh">
          {{ loading() ? '…' : 'Refresh' }}
        </button>
      </header>
      @if (error()) {
        <div class="orch-feed__error">{{ error() }}</div>
      }

      <app-token-summary-block [projectName]="projectName()" />

      @if (entries().length === 0 && !loading() && !error()) {
        <div class="orch-feed__empty">No orchestrator activity yet for this project.</div>
      }
      <ul class="orch-feed__list">
        @for (entry of reversed(); track entry.ts + entry.summary) {
          <li class="orch-feed__entry"
              [class.orch-feed__entry--decision]="entry.kind === 'decision'"
              [class.orch-feed__entry--action]="entry.kind === 'action'"
              [class.orch-feed__entry--observation]="entry.kind === 'observation'"
              [class.orch-feed__entry--intervention]="entry.kind === 'intervention'">
            <header class="orch-feed__entry-head">
              <span class="orch-feed__kind">{{ kindLabel(entry.kind) }}</span>
              <span class="orch-feed__topic">{{ entry.topic }}</span>
              <span class="orch-feed__ts">{{ formatTime(entry.ts) }}</span>
              @if (entry.tokenUsage; as tu) {
                <span class="orch-feed__tokens" [title]="tokenTooltip(tu)">
                  {{ tu.model || '?' }} · ↑{{ tu.inputTokens }} ↓{{ tu.outputTokens }}
                </span>
              }
            </header>
            <p class="orch-feed__summary">{{ entry.summary }}</p>
            @if (entry.reasoning) {
              <details class="orch-feed__reasoning">
                <summary>Why</summary>
                <p>{{ entry.reasoning }}</p>
              </details>
            }
            @if (entry.kind === 'decision' && entry.jobId) {
              @if (overridingTs() === entry.ts) {
                <div class="orch-feed__override-form">
                  <textarea class="orch-feed__override-input"
                            placeholder="Your direction. Will be sent as a Steer follow-up."
                            [(ngModel)]="overrideDraft"
                            data-testid="orchestrator-override-input"
                            rows="3"></textarea>
                  <div class="orch-feed__override-actions">
                    <button class="orch-feed__override-cancel"
                            (click)="cancelOverride()"
                            [disabled]="submittingOverride()">Cancel</button>
                    <button class="orch-feed__override-submit"
                            (click)="submitOverride(entry)"
                            [disabled]="submittingOverride() || !overrideDraft.trim()"
                            data-testid="orchestrator-override-submit">
                      {{ submittingOverride() ? 'Sending...' : 'Send override' }}
                    </button>
                  </div>
                </div>
              } @else {
                <button class="orch-feed__override"
                        (click)="startOverride(entry)"
                        data-testid="orchestrator-override-start"
                        title="Disagree with this decision? Send a Steer follow-up to the agent.">
                  Override this decision
                </button>
              }
            }
          </li>
        }
      </ul>
    </section>
  `,
  styles: [`
    :host { display: block; padding: 16px; max-width: 880px; margin: 0 auto; }
    .orch-feed__head {
      display: flex;
      align-items: baseline;
      gap: 12px;
      margin-bottom: 14px;
      padding-bottom: 8px;
      border-bottom: 1px solid rgba(255,255,255,0.08);
    }
    .orch-feed__title { margin: 0; color: #f9e2af; font-size: 1.05rem; letter-spacing: 0.04em; }
    .orch-feed__sub { color: rgba(255,255,255,0.55); font-size: 0.78rem; }
    .orch-feed__refresh {
      margin-left: auto;
      background: rgba(255,255,255,0.06);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.12);
      border-radius: 6px;
      padding: 4px 10px;
      font-size: 0.78rem;
      cursor: pointer;
    }
    .orch-feed__refresh:hover:not(:disabled) { background: rgba(255,255,255,0.1); }
    .orch-feed__refresh:disabled { opacity: 0.5; cursor: progress; }
    .orch-feed__error {
      color: #fda4af;
      background: rgba(244, 63, 94, 0.10);
      border: 1px solid rgba(244, 63, 94, 0.25);
      padding: 8px 10px;
      border-radius: 6px;
      margin-bottom: 12px;
    }
    .orch-feed__empty {
      color: rgba(255,255,255,0.5);
      font-style: italic;
      padding: 24px 0;
      text-align: center;
    }
    .orch-feed__list { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 10px; }
    .orch-feed__entry {
      padding: 10px 14px;
      border-radius: 8px;
      border: 1px solid rgba(255,255,255,0.10);
      background: rgba(15,23,42,0.55);
    }
    .orch-feed__entry--decision { border-color: rgba(196,181,253,0.30); background: rgba(76,29,149,0.12); }
    .orch-feed__entry--action { border-color: rgba(125,211,252,0.30); background: rgba(14,165,233,0.10); }
    .orch-feed__entry--observation { border-color: rgba(148,163,184,0.20); }
    .orch-feed__entry--intervention { border-color: rgba(249,226,175,0.40); background: rgba(249,226,175,0.10); }
    .orch-feed__entry-head {
      display: flex;
      align-items: baseline;
      gap: 10px;
      font-size: 0.72rem;
      color: rgba(255,255,255,0.6);
      letter-spacing: 0.04em;
      text-transform: uppercase;
      margin-bottom: 4px;
      flex-wrap: wrap;
    }
    .orch-feed__kind { font-weight: 700; color: #cdd6f4; }
    .orch-feed__topic { padding: 1px 6px; border-radius: 4px; background: rgba(255,255,255,0.06); }
    .orch-feed__ts { margin-left: auto; font-variant-numeric: tabular-nums; text-transform: none; letter-spacing: 0; }
    .orch-feed__tokens {
      font-family: var(--font-mono, monospace);
      font-size: 0.7rem;
      color: #94a3b8;
      cursor: help;
      text-transform: none;
      letter-spacing: 0;
    }
    .orch-feed__summary { color: #e2e8f0; margin: 2px 0 4px; font-size: 0.88rem; line-height: 1.45; }
    .orch-feed__reasoning summary {
      cursor: pointer;
      color: rgba(255,255,255,0.55);
      font-size: 0.78rem;
      user-select: none;
    }
    .orch-feed__reasoning summary:hover { color: #cdd6f4; }
    .orch-feed__reasoning p { margin: 6px 0 0; color: rgba(255,255,255,0.75); font-size: 0.84rem; }
    /*
     * Override controls. The plain "Override this decision" link sits
     * unobtrusively under the summary; clicking it expands an inline
     * textarea + Send/Cancel pair. Loud styling on Send so the user is
     * sure they are taking an action that goes back to the agent.
     */
    .orch-feed__override {
      margin-top: 6px;
      background: transparent;
      border: 1px dashed rgba(249, 226, 175, 0.35);
      color: #fcd34d;
      border-radius: 6px;
      padding: 4px 10px;
      font-size: 0.78rem;
      cursor: pointer;
    }
    .orch-feed__override:hover { background: rgba(249, 226, 175, 0.08); }
    .orch-feed__override-form {
      margin-top: 8px;
      display: flex;
      flex-direction: column;
      gap: 6px;
      padding: 8px;
      border: 1px solid rgba(249, 226, 175, 0.40);
      border-radius: 8px;
      background: rgba(249, 226, 175, 0.06);
    }
    .orch-feed__override-input {
      width: 100%;
      box-sizing: border-box;
      background: rgba(0,0,0,0.30);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 6px;
      padding: 6px 8px;
      font-family: inherit;
      font-size: 0.85rem;
      resize: vertical;
      min-height: 60px;
    }
    .orch-feed__override-input:focus { outline: none; border-color: rgba(249, 226, 175, 0.60); }
    .orch-feed__override-actions { display: flex; gap: 8px; justify-content: flex-end; }
    .orch-feed__override-cancel,
    .orch-feed__override-submit {
      border-radius: 6px;
      padding: 4px 12px;
      font-size: 0.80rem;
      cursor: pointer;
      border: 1px solid transparent;
    }
    .orch-feed__override-cancel {
      background: rgba(255,255,255,0.06);
      color: #cdd6f4;
      border-color: rgba(255,255,255,0.12);
    }
    .orch-feed__override-cancel:hover:not(:disabled) { background: rgba(255,255,255,0.10); }
    .orch-feed__override-submit {
      background: rgba(249, 226, 175, 0.20);
      color: #1e1e2e;
      border-color: rgba(249, 226, 175, 0.50);
      font-weight: 700;
    }
    .orch-feed__override-submit:hover:not(:disabled) { background: rgba(249, 226, 175, 0.35); }
    .orch-feed__override-submit:disabled,
    .orch-feed__override-cancel:disabled { opacity: 0.5; cursor: not-allowed; }
  `]
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
