import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { AnalysisReportService } from '../../../services/analysis-report.service';
import {
  ANALYSIS_CADENCES,
  ANALYSIS_TOPICS,
  AnalysisReport,
} from '../../../models/analysis-report.model';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';

/**
 * Project-level Analysis Reports surface (ROADMAP "Analysis Reports and
 * Meta-Actions"). Three things in one section:
 *
 * 1. Manual-trigger buttons for the fixed topic catalogue (roadmap alignment,
 *    queue health, docs drift, stale jobs, token spend, QA status).
 * 2. Scheduling controls per topic, default <code>disabled</code>. The cadence
 *    is persisted in <code>ProjectSettings.AnalysisSchedules</code>; the
 *    backend does not auto-run scheduled reports yet, see
 *    <code>docs/analysis-reports.md</code>.
 * 3. Report history newest-first with title, scope, producer, trigger,
 *    severity, parse status and a count of follow-ups. Click a row to
 *    drill down (event surfaced through <code>(openReport)</code>).
 *
 * Polls every 10 s while mounted. Empty state is intentional and explicit:
 * the user should see the surface even when nothing has been run yet.
 */
@Component({
  selector: 'app-project-analysis-reports-section',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="proj-detail__group" data-testid="project-analysis-reports-section">
      <h3>
        <span class="par__icon">📋</span>
        Analysis reports
        <span class="par__pill" data-testid="project-analysis-reports-count">
          {{ reports().length }} {{ reports().length === 1 ? 'report' : 'reports' }}
        </span>
      </h3>

      <p class="proj-detail__hint">
        Durable inspection records from manual buttons, scheduled cadences, the meta-cycle, and external monitors.
        Markdown is the human artifact; JSON is the additive convenience. Failed parses do not hide the body.
      </p>

      <h4 class="par__sub">Run an inspection</h4>
      <div class="par__triggers" data-testid="project-analysis-reports-triggers">
        @for (t of topics; track t.slug) {
          <button class="par__btn"
                  [attr.data-testid]="'project-analysis-trigger-' + t.slug"
                  [title]="t.description"
                  [disabled]="triggering() === t.slug"
                  (click)="trigger(t.slug)">
            {{ t.label }}
            @if (triggering() === t.slug) {
              <span class="par__btn-spin">…</span>
            }
          </button>
        }
      </div>
      @if (lastError()) {
        <p class="par__error" data-testid="project-analysis-reports-error">{{ lastError() }}</p>
      }

      <h4 class="par__sub">Schedule</h4>
      <p class="proj-detail__hint">
        Default is off. Scheduled execution is opt-in and never auto-runs without your explicit choice.
      </p>
      <table class="par__schedule" data-testid="project-analysis-reports-schedule">
        <thead>
          <tr><th>Topic</th><th>Cadence</th></tr>
        </thead>
        <tbody>
          @for (t of topics; track t.slug) {
            <tr>
              <td>
                <span class="par__topic-label">{{ t.label }}</span>
                <span class="par__topic-desc">{{ t.description }}</span>
              </td>
              <td>
                <select class="par__select"
                        [attr.data-testid]="'project-analysis-schedule-' + t.slug"
                        [ngModel]="scheduleFor(t.slug)"
                        (ngModelChange)="onScheduleChange(t.slug, $event)">
                  @for (c of cadences; track c.id) {
                    <option [value]="c.id" [title]="c.tooltip">{{ c.label }}</option>
                  }
                </select>
              </td>
            </tr>
          }
        </tbody>
      </table>

      <h4 class="par__sub">History</h4>
      @if (loading() && reports().length === 0) {
        <p class="proj-detail__empty">Loading…</p>
      } @else if (reports().length === 0) {
        <p class="proj-detail__empty" data-testid="project-analysis-reports-empty">
          No reports yet. Click an inspection button above to produce the first one.
        </p>
      } @else {
        <ul class="par__list" data-testid="project-analysis-reports-list">
          @for (r of reports(); track r.reportId) {
            <li class="par__item"
                [class.par__item--info]="r.severity === 'Info'"
                [class.par__item--warn]="r.severity === 'Warn'"
                [class.par__item--high]="r.severity === 'High'"
                [class.par__item--critical]="r.severity === 'Critical'"
                [attr.data-testid]="'project-analysis-report-row'"
                (click)="openReport.emit(r)">
              <header>
                <span class="par__sev par__sev--{{ r.severity.toLowerCase() }}">{{ r.severity }}</span>
                <span class="par__topic">{{ r.topic }}</span>
                <span class="par__trigger" [title]="'Trigger: ' + r.trigger">{{ triggerLabel(r.trigger) }}</span>
                @if (r.parseStatus !== 'Structured') {
                  <span class="par__parse par__parse--bad"
                        [attr.data-testid]="'project-analysis-report-parse-' + r.parseStatus.toLowerCase()"
                        [title]="parseTooltip(r)">
                    ⚠️ {{ parseLabel(r.parseStatus) }}
                  </span>
                }
                <span class="par__ts">{{ formatTime(r.createdAt) }}</span>
              </header>
              <p class="par__summary">{{ r.summary }}</p>
              <footer>
                <span class="par__meta">scope: {{ scopeLabel(r) }}</span>
                <span class="par__meta">producer: {{ producerLabel(r) }}</span>
                @if (r.followUpTaskSuggestions.length > 0) {
                  <span class="par__meta par__meta--accent">{{ r.followUpTaskSuggestions.length }} follow-up{{ r.followUpTaskSuggestions.length === 1 ? '' : 's' }}</span>
                }
                @if (r.references.length > 0) {
                  <span class="par__meta">{{ r.references.length }} ref{{ r.references.length === 1 ? '' : 's' }}</span>
                }
              </footer>
            </li>
          }
        </ul>
      }

      <div class="par__actions">
        <button class="par__btn par__btn--ghost"
                data-testid="project-analysis-reports-refresh"
                (click)="refresh()">Refresh</button>
      </div>
    </section>
  `,
  styles: [`
    :host { display: block; }
    .par__icon { margin-right: 6px; }
    .par__pill {
      font-size: 0.7rem;
      padding: 1px 8px;
      border-radius: 999px;
      background: rgba(255,255,255,0.10);
      color: #cdd6f4;
      margin-left: 8px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .par__sub {
      font-size: 0.78rem;
      color: rgba(255,255,255,0.65);
      margin: 14px 0 6px;
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }
    .par__triggers { display: flex; flex-wrap: wrap; gap: 6px; margin-bottom: 6px; }
    .par__btn {
      background: rgba(137,180,250,0.10);
      color: #cdd6f4;
      border: 1px solid rgba(137,180,250,0.35);
      border-radius: 6px;
      padding: 4px 10px;
      font-size: 0.82rem;
      cursor: pointer;
    }
    .par__btn:hover:not([disabled]) { background: rgba(137,180,250,0.18); }
    .par__btn[disabled] { opacity: 0.55; cursor: progress; }
    .par__btn--ghost {
      background: rgba(255,255,255,0.06);
      border-color: rgba(255,255,255,0.12);
    }
    .par__btn-spin { margin-left: 4px; opacity: 0.8; }
    .par__error {
      color: #fda4af;
      background: rgba(243,139,168,0.10);
      border: 1px solid rgba(243,139,168,0.30);
      padding: 4px 8px;
      border-radius: 4px;
      font-size: 0.8rem;
      margin: 6px 0 0;
    }

    .par__schedule {
      width: 100%;
      border-collapse: collapse;
      font-size: 0.82rem;
      margin: 6px 0 14px;
    }
    .par__schedule th {
      text-align: left;
      font-weight: 600;
      color: rgba(255,255,255,0.55);
      font-size: 0.72rem;
      text-transform: uppercase;
      letter-spacing: 0.04em;
      padding: 4px 8px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .par__schedule td {
      padding: 6px 8px;
      vertical-align: top;
      border-bottom: 1px solid rgba(255,255,255,0.04);
    }
    .par__topic-label { display: block; color: #cdd6f4; }
    .par__topic-desc { display: block; color: rgba(255,255,255,0.55); font-size: 0.72rem; }
    .par__select {
      background: rgba(0,0,0,0.30);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 5px;
      padding: 3px 6px;
      font-size: 0.80rem;
    }

    .par__list { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 6px; }
    .par__item {
      padding: 8px 10px;
      border-left: 2px solid rgba(255,255,255,0.10);
      background: rgba(255,255,255,0.03);
      border-radius: 0 4px 4px 0;
      cursor: pointer;
    }
    .par__item:hover { background: rgba(255,255,255,0.06); }
    .par__item--info { border-left-color: rgba(148,163,184,0.45); }
    .par__item--warn { border-left-color: #f9e2af; }
    .par__item--high { border-left-color: #fab387; background: rgba(250,179,135,0.06); }
    .par__item--critical { border-left-color: #f38ba8; background: rgba(243,139,168,0.10); }
    .par__item header {
      display: flex;
      flex-wrap: wrap;
      align-items: center;
      gap: 8px;
      font-size: 0.78rem;
      margin-bottom: 4px;
    }
    .par__sev {
      text-transform: uppercase;
      font-weight: 600;
      font-size: 0.70rem;
      padding: 1px 6px;
      border-radius: 3px;
      background: rgba(255,255,255,0.08);
      color: #cdd6f4;
    }
    .par__sev--info { background: rgba(148,163,184,0.18); color: #cbd5e1; }
    .par__sev--warn { background: rgba(249,226,175,0.20); color: #f9e2af; }
    .par__sev--high { background: rgba(250,179,135,0.20); color: #fab387; }
    .par__sev--critical { background: rgba(243,139,168,0.22); color: #f38ba8; }
    .par__topic { color: #89b4fa; font-family: ui-monospace, monospace; font-size: 0.78rem; }
    .par__trigger {
      color: rgba(255,255,255,0.55);
      font-size: 0.72rem;
      padding: 1px 5px;
      border-radius: 3px;
      background: rgba(255,255,255,0.05);
    }
    .par__parse {
      font-size: 0.72rem;
      padding: 1px 5px;
      border-radius: 3px;
    }
    .par__parse--bad { color: #f9e2af; background: rgba(249,226,175,0.15); }
    .par__ts {
      margin-left: auto;
      color: rgba(255,255,255,0.50);
      font-variant-numeric: tabular-nums;
      font-size: 0.72rem;
    }
    .par__summary { margin: 0 0 4px; color: #cdd6f4; font-size: 0.85rem; line-height: 1.45; }
    .par__item footer { display: flex; flex-wrap: wrap; gap: 10px; font-size: 0.72rem; color: rgba(255,255,255,0.55); }
    .par__meta--accent { color: #94e2d5; }

    .par__actions { display: flex; gap: 6px; margin-top: 14px; flex-wrap: wrap; }
  `]
})
export class ProjectAnalysisReportsSectionComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();
  readonly openReport = output<AnalysisReport>();

  private readonly svc = inject(AnalysisReportService);
  private timer?: VisibleIntervalHandle;

  readonly topics = ANALYSIS_TOPICS;
  readonly cadences = ANALYSIS_CADENCES;

  readonly reports = signal<AnalysisReport[]>([]);
  readonly schedule = signal<Record<string, string>>({});
  readonly loading = signal<boolean>(false);
  readonly triggering = signal<string | null>(null);
  readonly lastError = signal<string | null>(null);

  readonly hasUnstructured = computed(() => this.reports().some(r => r.parseStatus !== 'Structured'));

  ngOnInit(): void {
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(true), 10_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearVisibleInterval(this.timer);
  }

  refresh(silent = false): void {
    void silent;
    const project = this.projectName();
    if (!project) return;
    this.loading.set(true);
    this.svc.list(project, { limit: 100 }).subscribe({
      next: (resp) => {
        this.reports.set(resp.reports ?? []);
        this.loading.set(false);
      },
      error: () => { this.loading.set(false); },
    });
    this.svc.getSchedule(project).subscribe({
      next: (map) => this.schedule.set(map ?? {}),
      error: () => { /* silent; keep last */ },
    });
  }

  scheduleFor(topic: string): string {
    return this.schedule()[topic] ?? 'disabled';
  }

  onScheduleChange(topic: string, cadence: string): void {
    const project = this.projectName();
    if (!project) return;
    // Optimistic update so the select reflects the choice immediately.
    this.schedule.update(m => ({ ...m, [topic]: cadence }));
    this.svc.setSchedule(project, topic, cadence).subscribe({
      next: (m) => this.schedule.set(m ?? {}),
      error: () => { /* leave optimistic value; refresh will reconcile */ },
    });
  }

  trigger(topic: string): void {
    const project = this.projectName();
    if (!project) return;
    this.triggering.set(topic);
    this.lastError.set(null);
    this.svc.trigger(project, topic).subscribe({
      next: () => {
        this.triggering.set(null);
        this.refresh(true);
      },
      error: (err) => {
        this.triggering.set(null);
        this.lastError.set(err?.error?.error ?? 'Failed to run inspection.');
      },
    });
  }

  triggerLabel(t: string): string {
    switch (t) {
      case 'Manual': return 'manual';
      case 'Scheduled': return 'scheduled';
      case 'MetaCycle': return 'meta-cycle';
      case 'SupportingAgent': return 'supporting-agent';
      case 'ExternalMonitor': return 'external-monitor';
      default: return t.toLowerCase();
    }
  }

  parseLabel(s: string): string {
    return s === 'MalformedJson' ? 'malformed JSON' : 'unstructured';
  }

  parseTooltip(r: AnalysisReport): string {
    if (r.parseStatus === 'MalformedJson')
      return `Sidecar JSON failed to parse: ${r.parseError ?? 'unknown error'}. The Markdown body is still readable.`;
    if (r.parseStatus === 'Unstructured')
      return 'No JSON sidecar was written. The Markdown body is still readable.';
    return '';
  }

  scopeLabel(r: AnalysisReport): string {
    const k = r.scope?.kind ?? 'Project';
    if (k === 'Task' && r.scope?.jobId) return `task / ${r.scope.jobId}`;
    if (k === 'Run' && r.scope?.jobId) return `run / ${r.scope.jobId}#${r.scope.runIndex ?? '?'}`;
    if (k === 'Workspace') return 'workspace';
    if (k === 'TimeWindow') return 'time-window';
    return 'project';
  }

  producerLabel(r: AnalysisReport): string {
    const k = r.producer?.kind ?? 'Manual';
    return k.charAt(0).toLowerCase() + k.slice(1);
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
}
