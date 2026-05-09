import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Observable, map } from 'rxjs';
import { DriftService } from '../services/drift.service';
import { JobService } from '../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../utils/visible-interval';
import {
  DriftArchitectureElement,
  DriftArchitectureModel,
  DriftFindingStatus,
  DriftSeverity,
  ElementStateOverride,
} from '../models/drift.model';

interface MarbleRow {
  element: DriftArchitectureElement;
  effectiveStatus: DriftFindingStatus;
  evidenceCount: number;
}

/**
 * Project-level Drift surface, architecture-marble region (ROADMAP "Drift
 * Control" - "marble-style architecture map with at most ten elements").
 * Renders the architecture model carried by the most recent drift report
 * as scan-friendly cards, each with a per-element software-drift score,
 * severity, source coverage, latest finding summary, and tracking state.
 *
 * Five surface states the spec calls out:
 *  - no architecture model (no drift report, or no model carried)
 *  - healthy map (all bands light)
 *  - warning / critical element (band-coloured marble, drilldown highlighted)
 *  - element drill-down (evidence + actions)
 *  - invalid drift JSON fallback (error pill, no marble grid)
 *
 * The map never gates pickup or transitions in V1 (taxonomy: drift scores are
 * triage signals with evidence links, not automatic decisions).
 */
@Component({
  selector: 'app-project-drift-section',
  standalone: true,
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="proj-detail__group" data-testid="project-drift-section">
      <h3>
        <span class="pdrift__icon">🧭</span>
        Architecture marble drift
        @if (model(); as m) {
          <span class="pdrift__count" data-testid="project-drift-element-count">
            {{ m.elements.length }} / 10 elements
          </span>
        }
      </h3>

      <p class="proj-detail__hint">
        High-level architecture map for the project. Scores compare each
        element to recent code, schemas, tests, runtime evidence, and recent
        job evidence; lower means more drift risk. Up to ten elements per
        report so the surface stays scan-friendly.
      </p>

      @if (loading() && !model() && !error()) {
        <p class="proj-detail__empty" data-testid="project-drift-loading">Loading architecture map…</p>
      }

      @if (error(); as e) {
        <div class="pdrift__error" data-testid="project-drift-error">
          <strong>Drift report unreadable.</strong>
          <span>{{ e }}</span>
          <button class="pdrift__btn pdrift__btn--ghost"
                  data-testid="project-drift-retry"
                  (click)="refresh()">Retry</button>
        </div>
      }

      @if (!error() && !loading() && !model()) {
        <p class="proj-detail__empty" data-testid="project-drift-empty">
          No architecture model carried by any drift report yet.
          Run an architecture-aware drift action (for example
          <code>ADR / Code Drift</code>) to produce one. The marble surface
          shows up here as soon as a report carries an
          <code>architectureModel</code>.
        </p>
      }

      @if (model(); as m) {
        <div class="pdrift__map" data-testid="project-drift-map">
          @for (row of marbleRows(); track row.element.elementId) {
            <button class="pdrift__marble"
                    [class.pdrift__marble--info]="band(row.element.severity) === 'info'"
                    [class.pdrift__marble--warn]="band(row.element.severity) === 'warn'"
                    [class.pdrift__marble--high]="band(row.element.severity) === 'high'"
                    [class.pdrift__marble--critical]="band(row.element.severity) === 'critical'"
                    [class.pdrift__marble--active]="openedId() === row.element.elementId"
                    [attr.data-testid]="'project-drift-marble-' + row.element.elementId"
                    [attr.data-severity]="titleCase(row.element.severity)"
                    (click)="toggle(row.element.elementId)">
              <header>
                <span class="pdrift__marble-label">{{ row.element.label }}</span>
                <span class="pdrift__marble-score"
                      [attr.data-testid]="'project-drift-score-' + row.element.elementId">
                  {{ row.element.score }}
                </span>
              </header>
              <p class="pdrift__marble-role">{{ row.element.expectedRole }}</p>
              <footer>
                <span class="pdrift__sev pdrift__sev--{{ band(row.element.severity) }}"
                      [attr.data-testid]="'project-drift-severity-' + row.element.elementId">{{ titleCase(row.element.severity) }}</span>
                <span class="pdrift__cov" title="Source coverage (fraction of expected sources actually inspected)">
                  cov {{ formatCoverage(row.element.sourceCoverage) }}
                </span>
                <span class="pdrift__status pdrift__status--{{ row.effectiveStatus.toLowerCase() }}"
                      [attr.data-testid]="'project-drift-status-' + row.element.elementId">{{ titleCase(row.effectiveStatus) }}</span>
              </footer>
            </button>
          }
        </div>

        <p class="pdrift__source">
          Source: report
          <code data-testid="project-drift-source-report">{{ sourceReportId() ?? '(unknown)' }}</code>
          @if (sourceReportCreatedAt(); as ts) {
            <span> · {{ formatTime(ts) }}</span>
          }
          · model
          <code>{{ m.modelId }}</code>
        </p>

        @if (openedRow(); as row) {
          <section class="pdrift__panel" data-testid="project-drift-drilldown">
            <header>
              <h4>{{ row.element.label }}</h4>
              <button class="pdrift__close"
                      data-testid="project-drift-drilldown-close"
                      (click)="close()"
                      title="Close">×</button>
            </header>

            <dl class="pdrift__panel-meta">
              <div><dt>Score</dt><dd>{{ row.element.score }} ({{ titleCase(row.element.severity) }})</dd></div>
              <div><dt>Source coverage</dt><dd>{{ formatCoverage(row.element.sourceCoverage) }}</dd></div>
              <div><dt>Tracking</dt><dd>{{ titleCase(row.effectiveStatus) }}</dd></div>
              <div><dt>Expected role</dt><dd>{{ row.element.expectedRole }}</dd></div>
            </dl>

            @if (row.element.summary) {
              <p class="pdrift__panel-summary">{{ row.element.summary }}</p>
            }

            @if (row.element.guidelines && row.element.guidelines.length > 0) {
              <details class="pdrift__panel-details" open>
                <summary>Guidelines</summary>
                <ul>
                  @for (g of row.element.guidelines; track g) { <li>{{ g }}</li> }
                </ul>
              </details>
            }

            @if (row.element.allowedDependencies && row.element.allowedDependencies.length > 0) {
              <details class="pdrift__panel-details">
                <summary>Allowed dependencies</summary>
                <ul>
                  @for (d of row.element.allowedDependencies; track d) { <li><code>{{ d }}</code></li> }
                </ul>
              </details>
            }

            <details class="pdrift__panel-details" open>
              <summary>
                Evidence ({{ row.element.evidenceRefs.length }})
              </summary>
              @if (row.element.evidenceRefs.length === 0) {
                <p class="proj-detail__empty">No evidence references on this element.</p>
              } @else {
                <ul data-testid="project-drift-evidence">
                  @for (ref of row.element.evidenceRefs; track ref) {
                    <li><code>{{ ref }}</code></li>
                  }
                </ul>
              }
            </details>

            @if (row.element.sourceRefs && row.element.sourceRefs.length > 0) {
              <details class="pdrift__panel-details">
                <summary>Source set ({{ row.element.sourceRefs.length }})</summary>
                <ul>
                  @for (ref of row.element.sourceRefs; track ref) {
                    <li><code>{{ ref }}</code></li>
                  }
                </ul>
              </details>
            }

            @if (row.element.followUpTaskSuggestions && row.element.followUpTaskSuggestions.length > 0) {
              <details class="pdrift__panel-details">
                <summary>Follow-up suggestions</summary>
                <ul>
                  @for (f of row.element.followUpTaskSuggestions; track f) {
                    <li>{{ f }}</li>
                  }
                </ul>
              </details>
            }

            <div class="pdrift__actions" data-testid="project-drift-actions">
              <button class="pdrift__btn"
                      [attr.data-testid]="'project-drift-action-analyze-' + row.element.elementId"
                      [disabled]="busy() === 'analyze'"
                      (click)="analyzeElement(row.element)">
                Analyze this element
              </button>
              <button class="pdrift__btn"
                      [attr.data-testid]="'project-drift-action-followup-' + row.element.elementId"
                      [disabled]="busy() === 'followup'"
                      (click)="createFollowUp(row.element)">
                Create follow-up task
              </button>
              <span class="pdrift__action-divider"></span>
              <span class="pdrift__action-label">Mark</span>
              @for (s of statuses; track s) {
                <button class="pdrift__btn pdrift__btn--status"
                        [class.pdrift__btn--active]="titleCase(row.effectiveStatus) === s"
                        [attr.data-testid]="'project-drift-action-mark-' + s.toLowerCase() + '-' + row.element.elementId"
                        [disabled]="busy() === 'status'"
                        (click)="markStatus(row.element, s)">
                  {{ s }}
                </button>
              }
            </div>

            @if (actionMessage(); as msg) {
              <p class="pdrift__action-msg" data-testid="project-drift-action-msg">{{ msg }}</p>
            }
          </section>
        }
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .pdrift__icon { margin-right: 6px; }
    .pdrift__count {
      display: inline-block;
      margin-left: 8px;
      padding: 1px 8px;
      border-radius: 999px;
      background: rgba(255,255,255,0.06);
      color: #cbd5e1;
      font-size: 0.72rem;
      font-weight: 600;
    }

    .pdrift__error {
      display: flex;
      align-items: center;
      gap: 10px;
      flex-wrap: wrap;
      margin: 6px 0;
      padding: 8px 10px;
      border: 1px solid rgba(243,139,168,0.40);
      background: rgba(243,139,168,0.10);
      color: #fda4af;
      border-radius: 6px;
      font-size: 0.85rem;
    }
    .pdrift__error strong { color: #f38ba8; }

    .pdrift__map {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
      gap: 10px;
      margin: 10px 0 8px;
    }
    .pdrift__marble {
      text-align: left;
      padding: 10px 12px;
      border-radius: 10px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.10);
      color: #cdd6f4;
      cursor: pointer;
      display: flex;
      flex-direction: column;
      gap: 6px;
      min-height: 96px;
    }
    .pdrift__marble:hover { background: rgba(255,255,255,0.06); border-color: rgba(255,255,255,0.22); }
    .pdrift__marble header {
      display: flex;
      justify-content: space-between;
      align-items: baseline;
      gap: 8px;
    }
    .pdrift__marble-label { font-weight: 600; color: #f8fafc; font-size: 0.92rem; }
    .pdrift__marble-score {
      font-variant-numeric: tabular-nums;
      font-weight: 700;
      font-size: 1.05rem;
      color: #cbd5e1;
    }
    .pdrift__marble-role {
      margin: 0;
      color: rgba(205,214,244,0.75);
      font-size: 0.78rem;
      line-height: 1.35;
      flex-grow: 1;
    }
    .pdrift__marble footer {
      display: flex;
      align-items: center;
      gap: 6px;
      flex-wrap: wrap;
      font-size: 0.72rem;
    }
    .pdrift__marble--info       { border-left: 3px solid rgba(148,163,184,0.55); }
    .pdrift__marble--warn       { border-left: 3px solid #f9e2af; }
    .pdrift__marble--high       {
      border-left: 3px solid #fab387;
      background: rgba(250,179,135,0.06);
    }
    .pdrift__marble--critical   {
      border-left: 3px solid #f38ba8;
      background: rgba(243,139,168,0.10);
    }
    .pdrift__marble--active {
      outline: 1px solid rgba(196,181,253,0.55);
      box-shadow: 0 0 0 2px rgba(196,181,253,0.10);
    }

    .pdrift__sev {
      text-transform: uppercase;
      font-weight: 600;
      padding: 1px 6px;
      border-radius: 3px;
      background: rgba(255,255,255,0.08);
    }
    .pdrift__sev--info { background: rgba(148,163,184,0.18); color: #cbd5e1; }
    .pdrift__sev--warn { background: rgba(249,226,175,0.20); color: #f9e2af; }
    .pdrift__sev--high { background: rgba(250,179,135,0.20); color: #fab387; }
    .pdrift__sev--critical { background: rgba(243,139,168,0.22); color: #f38ba8; }

    .pdrift__cov { color: rgba(255,255,255,0.55); font-variant-numeric: tabular-nums; }

    .pdrift__status {
      padding: 1px 6px;
      border-radius: 3px;
      font-weight: 600;
      letter-spacing: 0.02em;
      background: rgba(255,255,255,0.06);
      color: #cbd5e1;
    }
    .pdrift__status--accepted { background: rgba(166,227,161,0.15); color: #a6e3a1; }
    .pdrift__status--tracked  { background: rgba(137,180,250,0.18); color: #89b4fa; }
    .pdrift__status--ignored  { background: rgba(255,255,255,0.06); color: rgba(255,255,255,0.55); text-decoration: line-through; }
    .pdrift__status--resolved { background: rgba(148,226,213,0.15); color: #94e2d5; }

    .pdrift__source {
      margin: 6px 0 0;
      color: rgba(255,255,255,0.45);
      font-size: 0.74rem;
    }
    .pdrift__source code { color: #c4b5fd; }

    .pdrift__panel {
      margin-top: 14px;
      padding: 12px 14px;
      background: rgba(0,0,0,0.30);
      border: 1px solid rgba(196,181,253,0.30);
      border-radius: 6px;
    }
    .pdrift__panel header {
      display: flex;
      align-items: baseline;
      justify-content: space-between;
      margin-bottom: 8px;
    }
    .pdrift__panel header h4 { margin: 0; color: #f8fafc; font-size: 0.95rem; }
    .pdrift__close {
      background: transparent;
      border: none;
      color: rgba(255,255,255,0.55);
      cursor: pointer;
      font-size: 1.1rem;
      line-height: 1;
    }
    .pdrift__close:hover { color: #f8fafc; }

    .pdrift__panel-meta {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 4px 12px;
      margin: 0 0 8px;
      font-size: 0.82rem;
    }
    .pdrift__panel-meta > div { display: contents; }
    .pdrift__panel-meta dt { color: rgba(255,255,255,0.55); }
    .pdrift__panel-meta dd { margin: 0; color: #cdd6f4; }

    .pdrift__panel-summary {
      margin: 0 0 10px;
      color: #e2e8f0;
      font-size: 0.85rem;
      line-height: 1.5;
    }

    .pdrift__panel-details { margin: 6px 0; }
    .pdrift__panel-details summary {
      cursor: pointer;
      color: rgba(255,255,255,0.65);
      font-size: 0.80rem;
      user-select: none;
    }
    .pdrift__panel-details ul {
      margin: 6px 0 0;
      padding-left: 20px;
      font-size: 0.82rem;
      color: #cdd6f4;
    }
    .pdrift__panel-details code {
      background: rgba(255,255,255,0.06);
      padding: 1px 4px;
      border-radius: 3px;
      font-size: 0.78rem;
      color: #c4b5fd;
    }

    .pdrift__actions {
      display: flex;
      gap: 6px;
      flex-wrap: wrap;
      align-items: center;
      margin-top: 10px;
    }
    .pdrift__action-label {
      color: rgba(255,255,255,0.55);
      font-size: 0.78rem;
      margin-left: 4px;
    }
    .pdrift__action-divider {
      width: 1px;
      align-self: stretch;
      background: rgba(255,255,255,0.10);
      margin: 0 6px;
    }
    .pdrift__btn {
      background: rgba(137,180,250,0.10);
      color: #cdd6f4;
      border: 1px solid rgba(137,180,250,0.35);
      border-radius: 6px;
      padding: 4px 10px;
      font-size: 0.80rem;
      cursor: pointer;
    }
    .pdrift__btn:hover:not([disabled]) { background: rgba(137,180,250,0.18); }
    .pdrift__btn[disabled] { opacity: 0.55; cursor: progress; }
    .pdrift__btn--ghost {
      background: rgba(255,255,255,0.06);
      border-color: rgba(255,255,255,0.12);
    }
    .pdrift__btn--status {
      background: rgba(255,255,255,0.05);
      border-color: rgba(255,255,255,0.12);
      color: rgba(255,255,255,0.75);
    }
    .pdrift__btn--status.pdrift__btn--active {
      background: rgba(196,181,253,0.18);
      border-color: rgba(196,181,253,0.55);
      color: #f8fafc;
      font-weight: 600;
    }

    .pdrift__action-msg {
      margin: 8px 0 0;
      color: #94e2d5;
      font-size: 0.78rem;
    }
  `]
})
export class ProjectDriftSectionComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly drift = inject(DriftService);
  private readonly jobs = inject(JobService);

  readonly model = signal<DriftArchitectureModel | null>(null);
  readonly sourceReportId = signal<string | null>(null);
  readonly sourceReportCreatedAt = signal<string | null>(null);
  readonly overrides = signal<ElementStateOverride[]>([]);
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);
  readonly openedId = signal<string | null>(null);
  readonly busy = signal<'analyze' | 'followup' | 'status' | null>(null);
  readonly actionMessage = signal<string | null>(null);

  readonly statuses: DriftFindingStatus[] = ['New', 'Tracked', 'Accepted', 'Ignored', 'Resolved'];

  readonly marbleRows = computed<MarbleRow[]>(() => {
    const m = this.model();
    if (!m) return [];
    const overrideMap = new Map<string, ElementStateOverride>(
      this.overrides().map(o => [o.elementId, o]),
    );
    return m.elements.map(el => ({
      element: el,
      effectiveStatus: overrideMap.get(el.elementId)?.status ?? el.status,
      evidenceCount: el.evidenceRefs.length,
    }));
  });

  readonly openedRow = computed<MarbleRow | null>(() => {
    const id = this.openedId();
    if (!id) return null;
    return this.marbleRows().find(r => r.element.elementId === id) ?? null;
  });

  private timer?: VisibleIntervalHandle;

  ngOnInit(): void {
    this.refresh();
    this.timer = setVisibleInterval(() => this.refresh(true), 15_000);
  }

  ngOnDestroy(): void {
    if (this.timer) clearVisibleInterval(this.timer);
  }

  refresh(silent = false): void {
    const project = this.projectName();
    if (!project) return;
    if (!silent) this.loading.set(true);
    this.drift.getArchitecture(project).subscribe({
      next: (resp) => {
        try {
          this.model.set(resp?.model ?? null);
          this.sourceReportId.set(resp?.sourceReportId ?? null);
          this.sourceReportCreatedAt.set(resp?.sourceReportCreatedAt ?? null);
          this.overrides.set(Array.isArray(resp?.overrides) ? resp.overrides : []);
          this.error.set(null);
        } catch (e) {
          this.error.set(this.describeError(e, 'Could not project drift response.'));
        }
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.describeError(err, 'Drift API call failed.'));
        this.loading.set(false);
      },
    });
  }

  toggle(elementId: string): void {
    this.openedId.update(curr => curr === elementId ? null : elementId);
    this.actionMessage.set(null);
  }

  close(): void {
    this.openedId.set(null);
    this.actionMessage.set(null);
  }

  band(severity: DriftSeverity | string): 'info' | 'warn' | 'high' | 'critical' {
    // Backend serializes enum names with the camelCase web-default policy
    // ("info" / "warn" / "high" / "critical"), but a fixture or future
    // producer may emit PascalCase. Normalize before matching so the marble
    // colour is correct in both shapes.
    switch ((severity ?? '').toString().toLowerCase()) {
      case 'critical': return 'critical';
      case 'high': return 'high';
      case 'warn': return 'warn';
      default: return 'info';
    }
  }

  /**
   * Display helper: render enum-style severity / status strings with a
   * leading capital regardless of wire case. Visible text is the contract
   * the spec asserts on; downstream comparisons (band(), markStatus()) are
   * case-insensitive on their own so the display value is the one the user
   * - and the e2e tests - read.
   */
  titleCase(value: string | null | undefined): string {
    if (!value) return '';
    const s = value.toString();
    return s.charAt(0).toUpperCase() + s.slice(1).toLowerCase();
  }

  formatCoverage(c: number): string {
    if (typeof c !== 'number' || Number.isNaN(c)) return '—';
    return `${Math.round(c * 100)}%`;
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

  markStatus(el: DriftArchitectureElement, status: DriftFindingStatus): void {
    const project = this.projectName();
    const m = this.model();
    if (!project || !m) return;
    this.busy.set('status');
    this.actionMessage.set(null);
    this.drift.setElementStatus(project, m.modelId, el.elementId, status).subscribe({
      next: (saved) => {
        this.overrides.update(curr => {
          const filtered = curr.filter(o => o.elementId !== saved.elementId || o.modelId !== saved.modelId);
          return [...filtered, saved];
        });
        this.busy.set(null);
        this.actionMessage.set(`Marked ${el.label} as ${status}.`);
      },
      error: (err) => {
        this.busy.set(null);
        this.actionMessage.set(this.describeError(err, 'Could not update status.'));
      },
    });
  }

  analyzeElement(el: DriftArchitectureElement): void {
    // V1: queue a follow-up task in 1-preparation that asks an agent to
    // analyze the element. Action surfaces are deliberately user-initiated;
    // the orchestrator never silently spawns a CLI from this button.
    const project = this.projectName();
    if (!project) return;
    this.busy.set('analyze');
    this.actionMessage.set(null);
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busy.set(null);
          this.actionMessage.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const slug = `analyze-arch-${el.elementId}-${Date.now().toString(36)}`;
        const promptMarkdown = this.buildAnalyzePrompt(el);
        this.jobs.createJob({
          id: slug,
          title: `Analyze architecture element: ${el.label}`,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: '1-preparation',
        }).subscribe({
          next: (resp) => {
            this.busy.set(null);
            this.actionMessage.set(`Queued analysis task ${resp?.id ?? slug} in 1-preparation.`);
          },
          error: (err) => {
            this.busy.set(null);
            this.actionMessage.set(this.describeError(err, 'Could not queue analysis task.'));
          },
        });
      },
      error: () => {
        this.busy.set(null);
        this.actionMessage.set('Could not resolve watch paths.');
      },
    });
  }

  createFollowUp(el: DriftArchitectureElement): void {
    const project = this.projectName();
    if (!project) return;
    this.busy.set('followup');
    this.actionMessage.set(null);
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busy.set(null);
          this.actionMessage.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const slug = `followup-arch-${el.elementId}-${Date.now().toString(36)}`;
        const seedSuggestion = (el.followUpTaskSuggestions ?? [])[0];
        const title = seedSuggestion
          ? `Follow-up: ${el.label} - ${seedSuggestion}`.slice(0, 90)
          : `Follow-up on ${el.label} drift`;
        const promptMarkdown = this.buildFollowUpPrompt(el, seedSuggestion);
        this.jobs.createJob({
          id: slug,
          title,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: '1-preparation',
        }).subscribe({
          next: (resp) => {
            this.busy.set(null);
            this.actionMessage.set(`Queued follow-up task ${resp?.id ?? slug} in 1-preparation.`);
          },
          error: (err) => {
            this.busy.set(null);
            this.actionMessage.set(this.describeError(err, 'Could not queue follow-up task.'));
          },
        });
      },
      error: () => {
        this.busy.set(null);
        this.actionMessage.set('Could not resolve watch paths.');
      },
    });
  }

  private resolveWatchPath(project: string): Observable<string | null> {
    return this.jobs.getWatchPaths().pipe(map(entries => {
      const match = entries.find(e => e.name === project)
        ?? entries.find(e => e.path === project);
      return match?.path ?? null;
    }));
  }

  private buildAnalyzePrompt(el: DriftArchitectureElement): string {
    const refs = el.evidenceRefs.map(r => `- \`${r}\``).join('\n');
    const sources = (el.sourceRefs ?? []).map(r => `- \`${r}\``).join('\n');
    return `# Analyze architecture element: ${el.label}

Spawned from the project Drift surface (architecture marble map).

**Element:** ${el.elementId}
**Expected role:** ${el.expectedRole}
**Current drift score:** ${el.score} / 100 (severity: ${el.severity})
**Source coverage:** ${(el.sourceCoverage * 100).toFixed(0)}%

## What to do

1. Inspect the element against its expected role.
2. Cross-check the listed evidence and source set for staleness or contradiction.
3. Produce a short findings note and propose follow-up tasks if drift is real.

## Evidence
${refs || '_(no evidence refs on the element)_'}

## Source set
${sources || '_(no source refs on the element)_'}
`;
  }

  private buildFollowUpPrompt(el: DriftArchitectureElement, seed: string | undefined): string {
    return `# Follow-up: ${el.label}

Created from the architecture marble drift surface.

**Element:** ${el.elementId}
**Drift score:** ${el.score} / 100 (severity: ${el.severity})
${seed ? `**Suggested action (from drift report):** ${seed}\n` : ''}
${el.summary ? `**Latest finding summary:**\n\n> ${el.summary}\n` : ''}

## What to do

Refine this scope before promoting to 2-ready: confirm the action, list affected files,
and decide whether the work belongs in a single task or several.
`;
  }

  private describeError(err: unknown, fallback: string): string {
    if (!err) return fallback;
    const e = err as { error?: { error?: string }; message?: string };
    return e.error?.error ?? e.message ?? fallback;
  }
}

