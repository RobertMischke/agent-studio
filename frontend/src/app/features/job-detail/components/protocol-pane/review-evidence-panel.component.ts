import { ChangeDetectionStrategy, Component, computed, input, output, signal } from '@angular/core';
import { CommonModule } from '@angular/common';
import { JobInfo, ReviewEvidenceEntry, ReviewEvidenceSeverity } from '../../../../models/job.model';

/**
 * Renders the per-task **review evidence** panel: findings from security
 * audits, code-review passes, task checks, or human notes that landed in
 * the job's `results/review-evidence.jsonl` file. The panel is purely
 * advisory — these findings are never blockers for state transitions.
 *
 * Each finding renders as a row with:
 *   - severity chip (info / warn / high),
 *   - source label, timestamp, run index when available,
 *   - title + body,
 *   - linked artifacts / file references,
 *   - "Acknowledge" toggle,
 *   - "Create follow-up task" action that posts to the API and emits
 *     the new job id so the parent can navigate.
 *
 * The component is presentational: data comes in via @Input, state changes
 * leave via @Output. The parent owns API calls and the routing decision
 * after a follow-up is created.
 */
@Component({
  selector: 'app-review-evidence-panel',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <section class="evidence-panel" data-testid="review-evidence-panel">
      <header class="evidence-panel__header">
        <h3 class="evidence-panel__title">
          <span class="evidence-panel__icon" aria-hidden="true">🔎</span>
          Review evidence
        </h3>
        @if (entries().length > 0) {
          <span class="evidence-panel__count" data-testid="review-evidence-count">
            {{ entries().length }} {{ entries().length === 1 ? 'finding' : 'findings' }}
          </span>
        }
      </header>

      @if (entries().length === 0) {
        <div class="evidence-panel__empty" data-testid="review-evidence-empty">
          No review findings recorded.
          <span class="evidence-panel__empty-hint">
            Audits and reviewers append rows to
            <code>results/review-evidence.jsonl</code>.
          </span>
        </div>
      } @else {
        <ul class="evidence-list">
          @for (e of sorted(); track e.id) {
            <li class="evidence-row"
                [attr.data-severity]="e.severity"
                [attr.data-acknowledged]="e.acknowledged ? 'true' : 'false'"
                [attr.data-testid]="'review-evidence-row-' + e.id">
              <div class="evidence-row__head">
                <span class="evidence-row__chip"
                      [attr.data-severity]="e.severity"
                      [attr.data-testid]="'review-evidence-severity-' + e.id">
                  {{ severityLabel(e.severity) }}
                </span>
                <span class="evidence-row__source">{{ sourceLabel(e.source) }}</span>
                <span class="evidence-row__ts" [title]="e.createdAt">{{ formatTime(e.createdAt) }}</span>
                @if (e.runIndex != null) {
                  <span class="evidence-row__run">run #{{ e.runIndex }}</span>
                }
                @if (e.acknowledged) {
                  <span class="evidence-row__ack-pill" data-testid="review-evidence-ack-pill">acknowledged</span>
                }
                @if (e.followupJobId) {
                  <span class="evidence-row__followup"
                        [attr.data-testid]="'review-evidence-followup-' + e.id"
                        title="Follow-up task created from this finding.">
                    follow-up: {{ e.followupJobId }}
                  </span>
                }
              </div>

              <div class="evidence-row__title" [attr.data-testid]="'review-evidence-title-' + e.id">
                {{ e.title }}
              </div>

              @if (e.body) {
                <div class="evidence-row__body" [attr.data-testid]="'review-evidence-body-' + e.id">
                  {{ e.body }}
                </div>
              }

              @if (e.artifacts.length > 0 || e.fileRefs.length > 0) {
                <ul class="evidence-row__refs">
                  @for (a of e.artifacts; track a) {
                    <li class="evidence-row__ref evidence-row__ref--artifact"
                        [attr.data-testid]="'review-evidence-artifact-' + e.id">
                      <span class="evidence-row__ref-icon" aria-hidden="true">📎</span>
                      <code>{{ a }}</code>
                    </li>
                  }
                  @for (f of e.fileRefs; track f) {
                    <li class="evidence-row__ref evidence-row__ref--file"
                        [attr.data-testid]="'review-evidence-fileref-' + e.id">
                      <span class="evidence-row__ref-icon" aria-hidden="true">📄</span>
                      <code>{{ f }}</code>
                    </li>
                  }
                </ul>
              }

              <div class="evidence-row__actions">
                <button type="button"
                        class="evidence-btn"
                        [attr.data-testid]="'review-evidence-toggle-ack-' + e.id"
                        [disabled]="busyId() === e.id"
                        (click)="onToggleAck(e)">
                  {{ e.acknowledged ? 'Mark unread' : 'Acknowledge' }}
                </button>
                <button type="button"
                        class="evidence-btn evidence-btn--primary"
                        [attr.data-testid]="'review-evidence-create-followup-' + e.id"
                        [disabled]="busyId() === e.id || !!e.followupJobId"
                        (click)="onCreateFollowup(e)">
                  @if (e.followupJobId) {
                    Follow-up created
                  } @else {
                    + Create follow-up task
                  }
                </button>
              </div>
            </li>
          }
        </ul>
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .evidence-panel {
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 10px;
      background: rgba(0,0,0,0.18);
      padding: 10px 12px;
      margin: 8px 0 12px;
    }
    .evidence-panel__header {
      display: flex;
      align-items: baseline;
      gap: 10px;
      margin-bottom: 8px;
    }
    .evidence-panel__title {
      margin: 0;
      font-size: 0.92rem;
      color: #cdd6f4;
      display: inline-flex;
      gap: 6px;
      align-items: center;
    }
    .evidence-panel__icon { font-size: 0.95rem; }
    .evidence-panel__count {
      color: rgba(205, 214, 244, 0.55);
      font-size: 0.78rem;
    }
    .evidence-panel__empty {
      display: flex;
      flex-direction: column;
      gap: 4px;
      padding: 10px 4px;
      color: rgba(205, 214, 244, 0.55);
      font-size: 0.83rem;
    }
    .evidence-panel__empty-hint { font-size: 0.78rem; opacity: 0.8; }
    .evidence-panel__empty-hint code {
      background: rgba(255,255,255,0.06);
      padding: 1px 4px;
      border-radius: 3px;
    }
    .evidence-list {
      list-style: none;
      padding: 0;
      margin: 0;
      display: flex;
      flex-direction: column;
      gap: 8px;
    }
    .evidence-row {
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 8px;
      padding: 8px 10px;
      background: rgba(255,255,255,0.025);
    }
    .evidence-row[data-severity="high"]    { border-left: 3px solid #f38ba8; }
    .evidence-row[data-severity="warn"]    { border-left: 3px solid #f9e2af; }
    .evidence-row[data-severity="info"]    { border-left: 3px solid #89b4fa; }
    .evidence-row[data-acknowledged="true"] { opacity: 0.78; }
    .evidence-row__head {
      display: flex;
      flex-wrap: wrap;
      gap: 8px;
      align-items: center;
      font-size: 0.72rem;
      color: rgba(205, 214, 244, 0.65);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .evidence-row__chip {
      display: inline-block;
      padding: 1px 6px;
      border-radius: 4px;
      font-weight: 600;
      letter-spacing: 0.06em;
    }
    .evidence-row__chip[data-severity="high"]  { background: rgba(243,139,168,0.18); color: #f38ba8; }
    .evidence-row__chip[data-severity="warn"]  { background: rgba(249,226,175,0.18); color: #f9e2af; }
    .evidence-row__chip[data-severity="info"]  { background: rgba(137,180,250,0.18); color: #89b4fa; }
    .evidence-row__source { color: rgba(205, 214, 244, 0.7); }
    .evidence-row__ts     { color: rgba(205, 214, 244, 0.5); font-family: var(--font-mono, monospace); text-transform: none; letter-spacing: 0; }
    .evidence-row__run    { color: rgba(205, 214, 244, 0.5); }
    .evidence-row__ack-pill {
      background: rgba(166,227,161,0.18);
      color: #a6e3a1;
      padding: 1px 6px;
      border-radius: 999px;
      letter-spacing: 0.06em;
    }
    .evidence-row__followup {
      color: #a6e3a1;
      text-transform: none;
      letter-spacing: 0;
    }
    .evidence-row__title {
      margin: 6px 0 2px;
      font-size: 0.92rem;
      color: #cdd6f4;
      font-weight: 600;
    }
    .evidence-row__body {
      font-size: 0.85rem;
      color: rgba(205, 214, 244, 0.85);
      white-space: pre-wrap;
      word-break: break-word;
    }
    .evidence-row__refs {
      list-style: none;
      padding: 0;
      margin: 6px 0 0;
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .evidence-row__ref {
      display: inline-flex;
      gap: 6px;
      align-items: center;
      font-size: 0.78rem;
      color: rgba(205, 214, 244, 0.7);
    }
    .evidence-row__ref code {
      background: rgba(255,255,255,0.05);
      padding: 1px 5px;
      border-radius: 3px;
      font-size: 0.78rem;
    }
    .evidence-row__actions {
      display: flex;
      gap: 8px;
      margin-top: 8px;
      flex-wrap: wrap;
    }
    .evidence-btn {
      background: rgba(255,255,255,0.05);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.12);
      padding: 4px 10px;
      border-radius: 6px;
      font-size: 0.78rem;
      cursor: pointer;
    }
    .evidence-btn:hover:not(:disabled) {
      border-color: rgba(255,255,255,0.28);
      background: rgba(255,255,255,0.08);
    }
    .evidence-btn:disabled { opacity: 0.45; cursor: not-allowed; }
    .evidence-btn--primary {
      background: rgba(137,180,250,0.18);
      color: #89b4fa;
      border-color: rgba(137,180,250,0.4);
    }
  `]
})
export class ReviewEvidencePanelComponent {
  readonly entries = input.required<ReviewEvidenceEntry[]>();
  readonly job = input.required<JobInfo>();

  readonly acknowledge = output<{ entry: ReviewEvidenceEntry; acknowledged: boolean }>();
  readonly createFollowup = output<ReviewEvidenceEntry>();

  /** Id of the row whose action is currently in flight (disables both buttons). */
  readonly busyId = signal<string | null>(null);

  /**
   * Stable order: high severity first, then warn, then info; ties broken by
   * createdAt ascending so the user reads findings chronologically inside a
   * severity bucket.
   */
  sorted = computed<ReviewEvidenceEntry[]>(() => {
    const rank: Record<ReviewEvidenceSeverity, number> = { high: 0, warn: 1, info: 2 };
    return [...this.entries()].sort((a, b) => {
      const ra = rank[a.severity] ?? 3;
      const rb = rank[b.severity] ?? 3;
      if (ra !== rb) return ra - rb;
      return new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime();
    });
  });

  severityLabel(s: ReviewEvidenceSeverity): string {
    if (s === 'high') return 'HIGH';
    if (s === 'warn') return 'WARN';
    return 'INFO';
  }

  sourceLabel(s: string): string {
    switch (s) {
      case 'security-audit': return 'Security audit';
      case 'code-review':    return 'Code review';
      case 'task-check':     return 'Task check';
      case 'human-note':     return 'Human note';
      default:               return 'Other';
    }
  }

  formatTime(iso: string): string {
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toISOString().replace('T', ' ').slice(0, 16) + 'Z';
    } catch {
      return iso;
    }
  }

  onToggleAck(e: ReviewEvidenceEntry): void {
    if (this.busyId()) return;
    this.busyId.set(e.id);
    this.acknowledge.emit({ entry: e, acknowledged: !e.acknowledged });
  }

  onCreateFollowup(e: ReviewEvidenceEntry): void {
    if (this.busyId() || e.followupJobId) return;
    this.busyId.set(e.id);
    this.createFollowup.emit(e);
  }

  /** Parent calls this once its API request resolves so the row re-enables. */
  clearBusy(): void {
    this.busyId.set(null);
  }
}
