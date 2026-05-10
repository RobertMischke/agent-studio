import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
} from '@angular/core';
import { HttpErrorResponse } from '@angular/common/http';
import { SecurityService } from '../../../../services/security.service';
import {
  SecurityBaselineBadge,
  SecurityBaselineResponse,
  SecurityReviewListResponse,
  SecurityReviewSummary,
} from './security-panel.types';
import { ConceptHelpComponent } from '../../../../components/concept-help/concept-help.component';

/**
 * Project Security panel (slice 1 of the quality-system mockup,
 * docs/mockups/quality-system/). Loads the baseline and review history
 * for one project and renders:
 *
 * <list type="bullet">
 *   <item>Header: title + baseline badge.</item>
 *   <item>Cards row: last review verdict, open-findings split, baseline definition link.</item>
 *   <item>Action buttons: Run security audit, Open evidence, Create follow-up task.</item>
 *   <item>Review history: newest first; <c>parseOk=false</c> rows show the
 *   "unstructured report" warning so the user still has a link to the
 *   evidence file (Report Contracts in the mockup README).</item>
 * </list>
 *
 * Action-driven principle: this component does NO analysis on its own
 * (per the mockup README). It only reads existing files via the
 * <c>SecurityService</c>; the "Run security audit" button delegates to
 * the backend, which queues a normal job that the orchestrator picks up.
 */
@Component({
  selector: 'app-security-panel',
  standalone: true,
  imports: [ConceptHelpComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="sec-panel" data-testid="security-panel">
      <header class="sec-panel__head">
        <div class="sec-panel__title-row">
          <h2 class="sec-panel__title">
            <span class="sec-panel__icon" aria-hidden="true">🔒</span>
            Security
            <app-concept-help concept="audits-and-checks" />
          </h2>
          <span class="sec-panel__badge"
                [class]="'sec-panel__badge--' + baselineBadge()"
                data-testid="security-baseline-badge">
            {{ baselineBadgeLabel() }}
          </span>
          <span class="sec-panel__spacer"></span>
          @if (loading()) {
            <span class="sec-panel__loading" data-testid="security-loading">Loading…</span>
          }
        </div>
        <p class="sec-panel__sub">Baseline, reviews, and active findings for this project.</p>
      </header>

      @if (loadError(); as err) {
        <div class="sec-panel__error" data-testid="security-load-error">
          Could not load security data: {{ err }}
        </div>
      }

      <div class="sec-panel__cards">
        <article class="sec-panel__card" data-testid="security-card-last-review">
          <h3 class="sec-panel__card-title">Last review</h3>
          @if (lastReview(); as r) {
            <p class="sec-panel__card-value">
              <span class="sec-panel__card-date">{{ r.reviewDate ?? formatDate(r.updatedAt) }}</span>
              <span class="sec-panel__card-verdict"
                    [class]="'sec-panel__card-verdict--' + verdictTone(r.verdict)">
                {{ r.verdict ?? 'no verdict' }}
              </span>
            </p>
            @if (r.title) {
              <p class="sec-panel__card-detail">{{ r.title }}</p>
            }
          } @else {
            <p class="sec-panel__card-empty">No reviews yet.</p>
          }
        </article>

        <article class="sec-panel__card" data-testid="security-card-open-findings">
          <h3 class="sec-panel__card-title">Open findings</h3>
          @if (lastReview()?.openFindings !== null && lastReview()?.openFindings !== undefined) {
            <p class="sec-panel__card-value sec-panel__card-value--big">{{ lastReview()?.openFindings }}</p>
            @if (severitySplitEntries().length > 0) {
              <ul class="sec-panel__sev-list">
                @for (s of severitySplitEntries(); track s.label) {
                  <li class="sec-panel__sev"
                      [class]="'sec-panel__sev--' + s.tone">
                    <span class="sec-panel__sev-label">{{ s.label }}</span>
                    <span class="sec-panel__sev-count">{{ s.count }}</span>
                  </li>
                }
              </ul>
            }
          } @else {
            <p class="sec-panel__card-empty">No findings reported.</p>
          }
        </article>

        <article class="sec-panel__card" data-testid="security-card-baseline-def">
          <h3 class="sec-panel__card-title">Baseline definition</h3>
          @if (baseline()?.exists) {
            <p class="sec-panel__card-value">
              {{ baseline()?.status ?? 'no status' }}
              @if (baseline()?.lastVerified) {
                <span class="sec-panel__card-detail">verified {{ baseline()?.lastVerified }}</span>
              }
            </p>
            @if (baseline()?.definitionRef; as ref) {
              <p class="sec-panel__card-link">
                <span class="sec-panel__card-link-label">Definition:</span>
                <code data-testid="security-baseline-def-ref">{{ ref }}</code>
              </p>
            } @else {
              <p class="sec-panel__card-detail">No definition record linked yet.</p>
            }
          } @else {
            <p class="sec-panel__card-empty">No baseline file yet ({{ baseline()?.filePath || 'not configured' }}).</p>
          }
        </article>
      </div>

      <div class="sec-panel__actions" data-testid="security-actions">
        <button type="button"
                class="sec-panel__btn sec-panel__btn--primary"
                data-testid="security-run-audit"
                [disabled]="auditBusy()"
                (click)="onRunAudit()">
          {{ auditBusy() ? 'Queueing…' : 'Run security audit' }}
        </button>
        <button type="button"
                class="sec-panel__btn"
                data-testid="security-open-evidence"
                [disabled]="!lastReview()"
                (click)="onOpenEvidence()">
          Open evidence
        </button>
        <button type="button"
                class="sec-panel__btn"
                data-testid="security-create-followup"
                (click)="onCreateFollowUp()">
          Create follow-up task
        </button>
        @if (auditError(); as err) {
          <span class="sec-panel__chip sec-panel__chip--error"
                role="status"
                data-testid="security-audit-error">{{ err }}</span>
        }
        @if (auditQueued(); as ok) {
          <span class="sec-panel__chip sec-panel__chip--ok"
                role="status"
                data-testid="security-audit-queued">{{ ok }}</span>
        }
      </div>

      <section class="sec-panel__history" data-testid="security-history">
        <h3 class="sec-panel__history-title">Review history</h3>
        @if (reviews().length === 0) {
          <p class="sec-panel__history-empty" data-testid="security-history-empty">
            No security reviews recorded yet.
            Click "Run security audit" to queue one.
          </p>
        } @else {
          <ul class="sec-panel__history-list">
            @for (r of reviews(); track r.fileName) {
              <li class="sec-panel__row"
                  [class.sec-panel__row--unstructured]="!r.parseOk"
                  [attr.data-testid]="'security-history-row'"
                  [attr.data-rel-path]="r.relPath"
                  [attr.data-parse-ok]="r.parseOk">
                <div class="sec-panel__row-main">
                  <span class="sec-panel__row-date">{{ r.reviewDate ?? formatDate(r.updatedAt) }}</span>
                  <span class="sec-panel__row-name">{{ r.title ?? r.fileName }}</span>
                  @if (r.verdict) {
                    <span class="sec-panel__row-verdict"
                          [class]="'sec-panel__row-verdict--' + verdictTone(r.verdict)">
                      {{ r.verdict }}
                    </span>
                  }
                </div>
                @if (!r.parseOk) {
                  <p class="sec-panel__warning" data-testid="security-unstructured-warning">
                    ⚠ unstructured report ({{ r.parseError ?? 'no structured block detected' }}). Raw Markdown shown below.
                  </p>
                  @if (rawCache()[r.fileName]; as raw) {
                    <pre class="sec-panel__raw" data-testid="security-raw-md">{{ raw }}</pre>
                  } @else {
                    <button type="button"
                            class="sec-panel__btn sec-panel__btn--ghost"
                            data-testid="security-row-load-raw"
                            (click)="loadRaw(r.fileName)">Load raw Markdown</button>
                  }
                } @else if (r.summary) {
                  <p class="sec-panel__row-summary">{{ r.summary }}</p>
                }
                <p class="sec-panel__row-link">
                  <code>{{ r.relPath }}</code>
                </p>
              </li>
            }
          </ul>
        }
      </section>
    </section>
  `,
  styles: [`
    :host { display: block; }

    .sec-panel { display: flex; flex-direction: column; gap: 18px; }

    .sec-panel__head {
      padding-bottom: 12px;
      border-bottom: 1px solid #313244;
    }
    .sec-panel__title-row { display: flex; align-items: center; gap: 12px; flex-wrap: wrap; }
    .sec-panel__title { margin: 0; font-size: 1.05rem; font-weight: 600; color: #f8fafc; display: flex; align-items: center; gap: 8px; }
    .sec-panel__icon { width: 18px; text-align: center; }
    .sec-panel__sub { margin: 6px 0 0; color: #a6adc8; font-size: 0.85rem; }
    .sec-panel__spacer { flex: 1; }
    .sec-panel__loading { color: #a6adc8; font-size: 0.78rem; }

    .sec-panel__badge {
      font-size: 0.72rem;
      letter-spacing: 0.02em;
      padding: 3px 9px;
      border-radius: 999px;
      border: 1px solid transparent;
      font-weight: 600;
    }
    .sec-panel__badge--ok { background: rgba(166, 227, 161, 0.16); color: #a6e3a1; border-color: rgba(166, 227, 161, 0.36); }
    .sec-panel__badge--stale { background: rgba(249, 226, 175, 0.16); color: #f9e2af; border-color: rgba(249, 226, 175, 0.36); }
    .sec-panel__badge--failing { background: rgba(243, 139, 168, 0.18); color: #f38ba8; border-color: rgba(243, 139, 168, 0.40); }
    .sec-panel__badge--missing { background: rgba(127, 132, 156, 0.16); color: #9399b2; border-color: rgba(127, 132, 156, 0.36); }
    .sec-panel__badge--unknown { background: rgba(127, 132, 156, 0.16); color: #9399b2; border-color: rgba(127, 132, 156, 0.36); }

    .sec-panel__error {
      padding: 10px 14px;
      background: rgba(243, 139, 168, 0.10);
      border: 1px solid rgba(243, 139, 168, 0.30);
      color: #f38ba8;
      border-radius: 6px;
      font-size: 0.85rem;
    }

    .sec-panel__cards {
      display: grid;
      grid-template-columns: repeat(3, minmax(0, 1fr));
      gap: 14px;
    }
    .sec-panel__card {
      background: #181825;
      border: 1px solid #313244;
      border-radius: 8px;
      padding: 14px 16px;
      min-height: 110px;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .sec-panel__card-title { margin: 0; color: #a6adc8; font-size: 0.74rem; letter-spacing: 0.06em; text-transform: uppercase; font-weight: 600; }
    .sec-panel__card-value { margin: 4px 0 0; color: #cdd6f4; font-size: 0.95rem; display: flex; align-items: baseline; gap: 8px; flex-wrap: wrap; }
    .sec-panel__card-value--big { font-size: 1.7rem; font-weight: 600; color: #f8fafc; }
    .sec-panel__card-date { font-weight: 600; color: #f8fafc; }
    .sec-panel__card-verdict {
      font-size: 0.72rem;
      letter-spacing: 0.02em;
      padding: 2px 8px;
      border-radius: 999px;
      border: 1px solid transparent;
      font-weight: 600;
    }
    .sec-panel__card-verdict--ok { background: rgba(166, 227, 161, 0.16); color: #a6e3a1; border-color: rgba(166, 227, 161, 0.36); }
    .sec-panel__card-verdict--stale { background: rgba(249, 226, 175, 0.16); color: #f9e2af; border-color: rgba(249, 226, 175, 0.36); }
    .sec-panel__card-verdict--fail { background: rgba(243, 139, 168, 0.18); color: #f38ba8; border-color: rgba(243, 139, 168, 0.40); }
    .sec-panel__card-verdict--unknown { background: rgba(127, 132, 156, 0.16); color: #9399b2; border-color: rgba(127, 132, 156, 0.36); }
    .sec-panel__card-detail { margin: 0; color: #a6adc8; font-size: 0.78rem; }
    .sec-panel__card-link { margin: 0; color: #a6adc8; font-size: 0.78rem; display: flex; gap: 6px; align-items: baseline; }
    .sec-panel__card-link-label { color: #6c7086; }
    .sec-panel__card-link code { background: #181825; border: 1px solid #313244; border-radius: 4px; padding: 1px 6px; color: #cdd6f4; font-size: 0.74rem; }
    .sec-panel__card-empty { margin: 0; color: #6c7086; font-size: 0.82rem; }

    .sec-panel__sev-list { list-style: none; margin: 6px 0 0; padding: 0; display: flex; gap: 6px; flex-wrap: wrap; }
    .sec-panel__sev {
      display: inline-flex;
      align-items: center;
      gap: 5px;
      font-size: 0.74rem;
      padding: 2px 8px;
      border-radius: 999px;
      background: #313244;
      color: #cdd6f4;
    }
    .sec-panel__sev--critical { background: rgba(243, 139, 168, 0.20); color: #f38ba8; }
    .sec-panel__sev--high { background: rgba(250, 179, 135, 0.20); color: #fab387; }
    .sec-panel__sev--medium { background: rgba(249, 226, 175, 0.18); color: #f9e2af; }
    .sec-panel__sev--low { background: rgba(166, 227, 161, 0.16); color: #a6e3a1; }
    .sec-panel__sev-label { text-transform: capitalize; }
    .sec-panel__sev-count { font-weight: 600; }

    .sec-panel__actions { display: flex; gap: 8px; flex-wrap: wrap; align-items: center; }
    .sec-panel__btn {
      background: #313244;
      color: #cdd6f4;
      border: 1px solid #45475a;
      padding: 6px 12px;
      border-radius: 6px;
      cursor: pointer;
      font: inherit;
      font-size: 0.82rem;
    }
    .sec-panel__btn:hover:not(:disabled) { background: #45475a; }
    .sec-panel__btn:disabled { opacity: 0.5; cursor: not-allowed; }
    .sec-panel__btn--primary {
      background: rgba(166, 227, 161, 0.18);
      color: #a6e3a1;
      border-color: rgba(166, 227, 161, 0.40);
    }
    .sec-panel__btn--primary:hover:not(:disabled) { background: rgba(166, 227, 161, 0.28); }
    .sec-panel__btn--ghost { background: transparent; border-style: dashed; color: #a6adc8; }
    .sec-panel__btn--ghost:hover:not(:disabled) { background: rgba(255,255,255,0.04); }

    .sec-panel__chip {
      font-size: 0.78rem;
      padding: 4px 10px;
      border-radius: 999px;
      border: 1px solid transparent;
    }
    .sec-panel__chip--error { background: rgba(243, 139, 168, 0.16); color: #f38ba8; border-color: rgba(243, 139, 168, 0.36); }
    .sec-panel__chip--ok { background: rgba(166, 227, 161, 0.16); color: #a6e3a1; border-color: rgba(166, 227, 161, 0.36); }

    .sec-panel__history-title { margin: 0 0 8px; color: #a6adc8; font-size: 0.74rem; letter-spacing: 0.06em; text-transform: uppercase; font-weight: 600; }
    .sec-panel__history-empty { margin: 0; color: #6c7086; font-size: 0.85rem; padding: 18px 0; }

    .sec-panel__history-list { list-style: none; margin: 0; padding: 0; display: flex; flex-direction: column; gap: 8px; }
    .sec-panel__row {
      background: #181825;
      border: 1px solid #313244;
      border-radius: 6px;
      padding: 10px 14px;
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .sec-panel__row--unstructured { border-color: rgba(249, 226, 175, 0.36); background: rgba(249, 226, 175, 0.05); }
    .sec-panel__row-main { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }
    .sec-panel__row-date { color: #f8fafc; font-weight: 600; font-size: 0.85rem; }
    .sec-panel__row-name { color: #cdd6f4; font-size: 0.85rem; flex: 1; min-width: 0; }
    .sec-panel__row-verdict {
      font-size: 0.70rem;
      padding: 2px 8px;
      border-radius: 999px;
      border: 1px solid transparent;
      font-weight: 600;
    }
    .sec-panel__row-verdict--ok { background: rgba(166, 227, 161, 0.16); color: #a6e3a1; border-color: rgba(166, 227, 161, 0.36); }
    .sec-panel__row-verdict--stale { background: rgba(249, 226, 175, 0.16); color: #f9e2af; border-color: rgba(249, 226, 175, 0.36); }
    .sec-panel__row-verdict--fail { background: rgba(243, 139, 168, 0.18); color: #f38ba8; border-color: rgba(243, 139, 168, 0.40); }
    .sec-panel__row-verdict--unknown { background: rgba(127, 132, 156, 0.16); color: #9399b2; border-color: rgba(127, 132, 156, 0.36); }
    .sec-panel__row-summary { margin: 0; color: #a6adc8; font-size: 0.80rem; }
    .sec-panel__row-link { margin: 0; }
    .sec-panel__row-link code { color: #6c7086; font-size: 0.72rem; background: #11111b; padding: 1px 6px; border-radius: 4px; }

    .sec-panel__warning {
      margin: 0;
      color: #f9e2af;
      font-size: 0.78rem;
      padding: 6px 10px;
      background: rgba(249, 226, 175, 0.08);
      border: 1px solid rgba(249, 226, 175, 0.24);
      border-radius: 4px;
    }
    .sec-panel__raw {
      margin: 0;
      max-height: 220px;
      overflow: auto;
      font-size: 0.74rem;
      background: #11111b;
      color: #cdd6f4;
      padding: 8px 10px;
      border-radius: 4px;
      border: 1px solid #313244;
      white-space: pre-wrap;
      word-break: break-word;
    }

    @media (max-width: 720px) {
      .sec-panel__cards { grid-template-columns: 1fr; }
    }
  `],
})
export class SecurityPanelComponent {
  private readonly security = inject(SecurityService);

  readonly projectName = input.required<string>();

  /** Emits when the user clicks "Create follow-up task". The host opens the create-job dialog. */
  readonly createFollowUp = output<{ projectName: string; prefill: string }>();

  /** Emits when "Open evidence" is clicked, with the relative path to the most recent review file. */
  readonly openEvidence = output<{ projectName: string; relPath: string }>();

  /** Emits when an audit is successfully queued; host can refresh the kanban or surface a chip. */
  readonly auditQueuedEvent = output<{ projectName: string; jobId: string }>();

  readonly loading = signal<boolean>(false);
  readonly loadError = signal<string | null>(null);
  readonly reviews = signal<SecurityReviewSummary[]>([]);
  readonly baseline = signal<SecurityBaselineResponse | null>(null);
  readonly auditBusy = signal<boolean>(false);
  readonly auditError = signal<string | null>(null);
  readonly auditQueued = signal<string | null>(null);
  /** Lazy-loaded raw Markdown bodies for parseOk=false rows, keyed by file name. */
  readonly rawCache = signal<Record<string, string>>({});

  readonly lastReview = computed<SecurityReviewSummary | null>(() => {
    const list = this.reviews();
    return list.length > 0 ? list[0] : null;
  });

  readonly baselineBadge = computed<SecurityBaselineBadge>(() => {
    const b = this.baseline();
    if (!b) return 'unknown';
    if (!b.exists) return 'missing';
    const status = (b.status ?? '').toLowerCase();
    if (!status) return 'unknown';
    if (status.includes('ok') || status.includes('pass')) return 'ok';
    if (status.includes('stale') || status.includes('warn')) return 'stale';
    if (status.includes('fail') || status.includes('crit')) return 'failing';
    return 'unknown';
  });

  readonly baselineBadgeLabel = computed<string>(() => {
    switch (this.baselineBadge()) {
      case 'ok': return 'Baseline OK';
      case 'stale': return 'Baseline stale';
      case 'failing': return 'Baseline failing';
      case 'missing': return 'No baseline';
      default: return 'Baseline unknown';
    }
  });

  readonly severitySplitEntries = computed(() => {
    const last = this.lastReview();
    if (!last?.severities) return [];
    const order = ['critical', 'high', 'medium', 'low'];
    const entries: { label: string; count: number; tone: string }[] = [];
    for (const key of order) {
      const value = last.severities[key];
      if (typeof value === 'number') entries.push({ label: key, count: value, tone: key });
    }
    // Append any extra severities the producer added (e.g. "info") so the
    // panel stays open to schema growth without losing data.
    for (const [key, value] of Object.entries(last.severities)) {
      if (!order.includes(key.toLowerCase())) {
        entries.push({ label: key, count: value, tone: 'medium' });
      }
    }
    return entries;
  });

  constructor() {
    // Reload whenever the project changes. The shell may switch projects
    // without unmounting the panel (rare today, but keeps the contract
    // correct if a future router does in-place navigation).
    effect(() => {
      const name = this.projectName();
      if (name) this.refresh(name);
    });
  }

  private refresh(name: string): void {
    this.loading.set(true);
    this.loadError.set(null);
    this.auditError.set(null);
    this.auditQueued.set(null);
    this.rawCache.set({});

    this.security.listReviews(name).subscribe({
      next: (res: SecurityReviewListResponse) => {
        this.reviews.set(res.reviews);
      },
      error: (err: HttpErrorResponse) => {
        this.loadError.set(err.message ?? 'unknown');
        this.reviews.set([]);
      },
    });

    this.security.getBaseline(name).subscribe({
      next: (b: SecurityBaselineResponse) => {
        this.baseline.set(b);
        this.loading.set(false);
      },
      error: (err: HttpErrorResponse) => {
        this.loadError.set(this.loadError() ?? err.message ?? 'unknown');
        this.baseline.set(null);
        this.loading.set(false);
      },
    });
  }

  onRunAudit(): void {
    const name = this.projectName();
    if (!name) return;
    if (this.auditBusy()) return;
    this.auditBusy.set(true);
    this.auditError.set(null);
    this.auditQueued.set(null);
    this.security.queueAudit(name).subscribe({
      next: (res) => {
        this.auditBusy.set(false);
        this.auditQueued.set(`Audit queued (${res.jobId}). It will start when the runner is idle.`);
        this.auditQueuedEvent.emit({ projectName: name, jobId: res.jobId });
      },
      error: (err: HttpErrorResponse) => {
        this.auditBusy.set(false);
        const body = err.error;
        const reason = body?.message ?? body?.error ?? err.message ?? 'audit failed';
        this.auditError.set(reason);
      },
    });
  }

  onOpenEvidence(): void {
    const last = this.lastReview();
    const name = this.projectName();
    if (!last || !name) return;
    this.openEvidence.emit({ projectName: name, relPath: last.relPath });
  }

  onCreateFollowUp(): void {
    const name = this.projectName();
    const last = this.lastReview();
    const lines: string[] = ['# Security follow-up', ''];
    if (last) {
      lines.push(`Source review: \`${last.relPath}\`${last.reviewDate ? ' (' + last.reviewDate + ')' : ''}.`);
      if (last.summary) {
        lines.push('');
        lines.push(last.summary);
      }
      if (typeof last.openFindings === 'number') {
        lines.push('');
        lines.push(`Open findings reported: ${last.openFindings}.`);
      }
    } else {
      lines.push('No prior review attached. Describe the finding before queueing.');
    }
    lines.push('');
    lines.push('Action: address the finding above. Treat the source review as evidence; do not silently mutate task state.');
    this.createFollowUp.emit({ projectName: name, prefill: lines.join('\n') });
  }

  loadRaw(fileName: string): void {
    const name = this.projectName();
    if (!name) return;
    if (this.rawCache()[fileName] !== undefined) return;
    this.security.readReview(name, fileName).subscribe({
      next: (res) => {
        const next = { ...this.rawCache(), [fileName]: res.content };
        this.rawCache.set(next);
      },
      error: (err: HttpErrorResponse) => {
        const next = { ...this.rawCache(), [fileName]: `Failed to load: ${err.message ?? 'unknown'}` };
        this.rawCache.set(next);
      },
    });
  }

  formatDate(iso: string): string {
    if (!iso) return '';
    // Display the date portion only; the panel header already shows
    // "Loading…" for in-flight states, so timestamps in cards stay terse.
    const idx = iso.indexOf('T');
    return idx > 0 ? iso.slice(0, idx) : iso;
  }

  verdictTone(verdict: string | null): string {
    if (!verdict) return 'unknown';
    const v = verdict.toLowerCase();
    if (v.includes('ok') || v.includes('pass')) return 'ok';
    if (v.includes('stale') || v.includes('warn')) return 'stale';
    if (v.includes('fail') || v.includes('crit')) return 'fail';
    return 'unknown';
  }
}
