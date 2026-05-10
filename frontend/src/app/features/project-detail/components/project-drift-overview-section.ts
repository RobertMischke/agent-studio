import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, signal } from '@angular/core';
import { Observable, map } from 'rxjs';
import { DriftService } from '../../../services/drift.service';
import { JobService } from '../../../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../utils/visible-interval';
import {
  DriftDimension,
  DriftDimensionType,
  DriftFinding,
  DriftFollowUpSuggestion,
  DriftReport,
  DriftReportDetailResponse,
  DriftScoreBand,
  DriftSeverity,
} from '../../../models/drift.model';

/**
 * Project-level Drift overview surface (ROADMAP "Drift Control",
 * design-principles "Drift is a scored project dimension"). Sits above the
 * existing architecture-marble section and answers four scan-level
 * questions for the latest drift report:
 *
 *  - overall score and band
 *  - dimension state (Intent, Spec, TaskJob, Architecture, Documentation,
 *    Marketing, Design, Test, Runtime, Process, Schema, Token)
 *  - latest findings and follow-up suggestions
 *  - report history with parse-status badges
 *
 * Action buttons cover the seven named drift comparisons; ADR / Code Drift
 * and Docs / Marketing Drift hit dedicated backend producers, the rest queue
 * a normal preparation task with a templated drift-analysis prompt so
 * "follow-up work becomes a normal queued task" stays the rule.
 *
 * Constraint: scores never hide evidence. Failed JSON sidecars surface a
 * warning chip while keeping the Markdown drilldown reachable; an empty
 * pile keeps the action buttons visible so the user can start one.
 */

interface DimensionCardRow {
  type: DriftDimensionType;
  label: string;
  dimension: DriftDimension | null;
}

const DIMENSION_LABELS: Record<DriftDimensionType, string> = {
  Intent: 'Intent',
  Spec: 'Spec',
  TaskJob: 'Task / Job',
  Architecture: 'Architecture',
  Documentation: 'Documentation',
  Marketing: 'Marketing',
  Design: 'Design',
  Test: 'Test',
  Runtime: 'Runtime',
  Process: 'Process',
  Schema: 'Schema',
  Token: 'Token',
};

const ALL_DIMENSIONS: DriftDimensionType[] = [
  'Intent', 'Spec', 'TaskJob', 'Architecture', 'Documentation', 'Marketing',
  'Design', 'Test', 'Runtime', 'Process', 'Schema', 'Token',
];

interface ActionButton {
  slug: string;
  label: string;
  description: string;
  /**
   * `inline` fires the matching backend POST and produces an evidence-only
   * drift report immediately. `queue` creates a 1-preparation task whose
   * prompt asks an agent to run the comparison; the returned job id is
   * surfaced for the user to promote when ready.
   */
  kind: 'inline' | 'queue';
  /** When kind=='queue', label of the relatedDimension on the prompt. */
  relatedDimension?: DriftDimensionType;
}

const ACTIONS: ActionButton[] = [
  { slug: 'analyze-project',  label: 'Analyze Project Drift',                  description: 'Score every drift dimension at once and write a project-level report.', kind: 'queue' },
  { slug: 'specs-tasks-jobs', label: 'Compare Specs to Tasks and Jobs',        description: 'Verify queue and job evidence still match written specs and prompts.', kind: 'queue', relatedDimension: 'Spec' },
  { slug: 'adrs-code',        label: 'Compare ADRs to Code',                   description: 'Run the ADR / Code Drift action against the current source tree.', kind: 'inline' },
  { slug: 'docs-marketing',   label: 'Compare Docs and Marketing to Product',  description: 'Run the Docs / Marketing Drift action against canonical project docs.', kind: 'inline' },
  { slug: 'design-screenshots', label: 'Compare Design to Screenshots',        description: 'Compare design references against the latest captured screenshots.', kind: 'queue', relatedDimension: 'Design' },
  { slug: 'tests-risk',       label: 'Compare Tests to Risk',                  description: 'Check whether tests still cover the documented risk areas.', kind: 'queue', relatedDimension: 'Test' },
  { slug: 'runtime-expectations', label: 'Compare Runtime to Expectations',    description: 'Compare runtime logs and observability against expected behaviour.', kind: 'queue', relatedDimension: 'Runtime' },
];

@Component({
  selector: 'app-project-drift-overview-section',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="proj-detail__group" data-testid="project-drift-overview-section">
      <h3>
        <span class="pdov__icon">📊</span>
        Drift overview
        @if (latest(); as r) {
          <span class="pdov__band pdov__band--{{ bandClass(r.scoreBand) }}"
                data-testid="project-drift-overview-band">{{ tc(r.scoreBand) }}</span>
        }
        @if (latest(); as r) {
          <span class="pdov__score-pill"
                data-testid="project-drift-overview-score">{{ r.overallScore }} / 100</span>
        }
      </h3>

      <p class="proj-detail__hint">
        Drift is the gap between what the project says and what the project does.
        Scores are triage signals with evidence links, not automatic decisions:
        the surface never blocks lane transitions in V1, and follow-up work
        always becomes a normal queued task.
      </p>

      @if (loading() && !latest() && reports().length === 0 && !error()) {
        <p class="proj-detail__empty" data-testid="project-drift-overview-loading">Loading drift reports…</p>
      }

      @if (error(); as e) {
        <div class="pdov__error" data-testid="project-drift-overview-error">
          <strong>Drift report unreadable.</strong>
          <span>{{ e }}</span>
          <button class="pdov__btn pdov__btn--ghost"
                  data-testid="project-drift-overview-retry"
                  (click)="refresh()">Retry</button>
        </div>
      }

      <h4 class="pdov__sub">Run a comparison</h4>
      <div class="pdov__actions" data-testid="project-drift-overview-actions">
        @for (a of actions; track a.slug) {
          <button class="pdov__btn"
                  [attr.data-testid]="'project-drift-overview-action-' + a.slug"
                  [title]="a.description"
                  [disabled]="busyAction() === a.slug"
                  (click)="runAction(a)">
            {{ a.label }}
            @if (busyAction() === a.slug) { <span class="pdov__btn-spin">…</span> }
          </button>
        }
      </div>
      @if (actionMessage(); as msg) {
        <p class="pdov__action-msg" data-testid="project-drift-overview-action-msg">{{ msg }}</p>
      }
      @if (actionError(); as err) {
        <p class="pdov__action-error" data-testid="project-drift-overview-action-error">{{ err }}</p>
      }

      @if (!error() && !loading() && reports().length === 0) {
        <p class="proj-detail__empty" data-testid="project-drift-overview-empty">
          No drift reports yet for this project.
          Run a comparison above to produce the first one;
          evidence-only reports are also fine while you wait for an agent verdict.
        </p>
      }

      @if (latest(); as r) {
        <div class="pdov__latest" data-testid="project-drift-overview-latest">
          <div class="pdov__latest-header">
            <div class="pdov__latest-headline">
              <p class="pdov__latest-summary">{{ r.summary }}</p>
              <p class="pdov__latest-meta">
                <code data-testid="project-drift-overview-latest-id">{{ r.reportId }}</code>
                · {{ formatTime(r.createdAt) }}
                · trigger: {{ tc(r.trigger) }}
                · scope: {{ scopeLabel(r) }}
              </p>
            </div>
            <button class="pdov__btn pdov__btn--ghost"
                    data-testid="project-drift-overview-open-latest"
                    (click)="openReport(r.reportId)">Open report</button>
          </div>

          @if (parseStatusEq(r.parseStatus, 'Unstructured')) {
            <div class="pdov__warn" data-testid="project-drift-overview-warn-unstructured">
              ⚠️ <strong>Unstructured report.</strong>
              The producer wrote Markdown only. Structured filters (severity counts, dimension cards,
              follow-up status) are best-effort.
            </div>
          } @else if (parseStatusEq(r.parseStatus, 'MalformedJson')) {
            <div class="pdov__warn pdov__warn--strong" data-testid="project-drift-overview-warn-malformed">
              ⚠️ <strong>Malformed JSON sidecar.</strong>
              Markdown stays readable below.
              @if (r.parseError) { <code>{{ r.parseError }}</code> }
            </div>
          }

          <h4 class="pdov__sub">Dimensions</h4>
          <div class="pdov__dimensions" data-testid="project-drift-overview-dimensions">
            @for (row of dimensionRows(); track row.type) {
              @if (row.dimension) {
                <button class="pdov__dim pdov__dim--{{ severityClass(row.dimension.severity) }}"
                        [attr.data-testid]="'project-drift-overview-dim-' + dimSlug(row.type)"
                        (click)="toggleDimension(row.type)">
                  <header>
                    <span class="pdov__dim-label">{{ row.label }}</span>
                    <span class="pdov__dim-score"
                          [attr.data-testid]="'project-drift-overview-dim-score-' + dimSlug(row.type)">
                      {{ row.dimension.score }}
                    </span>
                  </header>
                  <p class="pdov__dim-summary">{{ row.dimension.summary }}</p>
                  <footer>
                    <span class="pdov__sev pdov__sev--{{ severityClass(row.dimension.severity) }}"
                          [attr.data-testid]="'project-drift-overview-dim-sev-' + dimSlug(row.type)">{{ tc(row.dimension.severity) }}</span>
                    <span class="pdov__cov" title="Source coverage">cov {{ formatCoverage(row.dimension.sourceCoverage) }}</span>
                    <span class="pdov__status pdov__status--{{ statusClass(row.dimension.status) }}"
                          [attr.data-testid]="'project-drift-overview-dim-status-' + dimSlug(row.type)">{{ tc(row.dimension.status) }}</span>
                  </footer>
                </button>
              } @else {
                <div class="pdov__dim pdov__dim--empty"
                     [attr.data-testid]="'project-drift-overview-dim-' + dimSlug(row.type) + '-empty'">
                  <header>
                    <span class="pdov__dim-label">{{ row.label }}</span>
                    <span class="pdov__dim-score">—</span>
                  </header>
                  <p class="pdov__dim-summary pdov__dim-summary--muted">
                    Not scored in this report. Run a comparison that covers this dimension to populate it.
                  </p>
                </div>
              }
            }
          </div>

          @if (openedDimension(); as opened) {
            <section class="pdov__panel" data-testid="project-drift-overview-dimension-drilldown">
              <header>
                <h4>{{ opened.label }} <span class="pdov__panel-score">{{ opened.dimension!.score }} / 100</span></h4>
                <button class="pdov__close"
                        data-testid="project-drift-overview-dimension-close"
                        (click)="closeDimension()">×</button>
              </header>
              <p class="pdov__panel-summary">{{ opened.dimension!.summary }}</p>

              <dl class="pdov__panel-meta">
                <div><dt>Severity</dt><dd>{{ tc(opened.dimension!.severity) }}</dd></div>
                <div><dt>Status</dt><dd>{{ tc(opened.dimension!.status) }}</dd></div>
                <div><dt>Confidence</dt><dd>{{ formatConfidence(opened.dimension!.confidence) }}</dd></div>
                <div><dt>Source coverage</dt><dd>{{ formatCoverage(opened.dimension!.sourceCoverage) }}</dd></div>
              </dl>

              @if (opened.dimension!.evidenceRefs.length > 0) {
                <details class="pdov__panel-details" open>
                  <summary>Evidence ({{ opened.dimension!.evidenceRefs.length }})</summary>
                  <ul data-testid="project-drift-overview-dimension-evidence">
                    @for (ref of opened.dimension!.evidenceRefs; track ref) {
                      <li><code>{{ ref }}</code></li>
                    }
                  </ul>
                </details>
              }

              @if (opened.dimension!.recommendedActions.length > 0) {
                <details class="pdov__panel-details">
                  <summary>Recommended actions</summary>
                  <ul>
                    @for (a of opened.dimension!.recommendedActions; track a) {
                      <li>{{ a }}</li>
                    }
                  </ul>
                </details>
              }

              @if ((opened.dimension!.findings?.length ?? 0) > 0) {
                <h5 class="pdov__panel-sub">Findings</h5>
                <ul class="pdov__findings" data-testid="project-drift-overview-findings">
                  @for (f of opened.dimension!.findings; track f.findingId) {
                    <li class="pdov__finding pdov__finding--{{ severityClass(f.severity) }}"
                        [attr.data-testid]="'project-drift-overview-finding-' + f.findingId">
                      <header>
                        <span class="pdov__sev pdov__sev--{{ severityClass(f.severity) }}">{{ tc(f.severity) }}</span>
                        <span class="pdov__finding-id"><code>{{ f.findingId }}</code></span>
                        <span class="pdov__status pdov__status--{{ statusClass(f.status) }}">{{ tc(f.status) }}</span>
                      </header>
                      <p>{{ f.summary }}</p>
                      @if ((f.evidenceRefs?.length ?? 0) > 0) {
                        <details class="pdov__panel-details">
                          <summary>Evidence ({{ f.evidenceRefs!.length }})</summary>
                          <ul>
                            @for (ref of f.evidenceRefs!; track ref) {
                              <li><code>{{ ref }}</code></li>
                            }
                          </ul>
                        </details>
                      }
                      <div class="pdov__finding-actions">
                        <button class="pdov__btn pdov__btn--accent"
                                [attr.data-testid]="'project-drift-overview-finding-followup-' + f.findingId"
                                [disabled]="busyFollowUp() === f.findingId"
                                (click)="createFollowUpFromFinding(opened.dimension!, f)">
                          Create follow-up task
                        </button>
                      </div>
                    </li>
                  }
                </ul>
              }
            </section>
          }

          @if (r.followUpTaskSuggestions.length > 0) {
            <h4 class="pdov__sub">Follow-up suggestions</h4>
            <ul class="pdov__followups" data-testid="project-drift-overview-followups">
              @for (s of r.followUpTaskSuggestions; track s.title) {
                <li class="pdov__followup pdov__followup--{{ priorityClass(s.priority) }}">
                  <header>
                    <strong>{{ s.title }}</strong>
                    <span class="pdov__prio pdov__prio--{{ priorityClass(s.priority) }}">{{ tc(s.priority) }}</span>
                    @if (s.relatedDimension) {
                      <span class="pdov__rel">{{ DIMENSION_LABELS[s.relatedDimension] || s.relatedDimension }}</span>
                    }
                    @if (s.createdJobId) {
                      <span class="pdov__followup-job">queued · <code>{{ s.createdJobId }}</code></span>
                    }
                  </header>
                  @if (s.summary) { <p>{{ s.summary }}</p> }
                  @if (!s.createdJobId) {
                    <button class="pdov__btn pdov__btn--accent"
                            [attr.data-testid]="'project-drift-overview-followup-suggest-' + slug(s.title)"
                            [disabled]="busyFollowUp() === s.title"
                            (click)="createFollowUpFromSuggestion(s)">
                      Queue follow-up task
                    </button>
                  }
                </li>
              }
            </ul>
          }
        </div>
      }

      <h4 class="pdov__sub">Report history</h4>
      @if (reports().length === 0) {
        <p class="proj-detail__empty">No drift reports yet.</p>
      } @else {
        <ul class="pdov__history" data-testid="project-drift-overview-history">
          @for (r of reports(); track r.reportId) {
            <li class="pdov__hist-row pdov__hist-row--{{ bandClass(r.scoreBand) }}"
                [attr.data-testid]="'project-drift-overview-history-row'"
                (click)="openReport(r.reportId)">
              <span class="pdov__hist-band pdov__hist-band--{{ bandClass(r.scoreBand) }}">{{ tc(r.scoreBand) }}</span>
              <span class="pdov__hist-score">{{ r.overallScore }}</span>
              <span class="pdov__hist-summary">{{ r.summary }}</span>
              <span class="pdov__hist-trigger">{{ tc(r.trigger) }}</span>
              @if (!parseStatusEq(r.parseStatus, 'Structured')) {
                <span class="pdov__hist-parse"
                      [attr.data-testid]="'project-drift-overview-history-parse-' + (r.parseStatus ?? '').toString().toLowerCase()"
                      [title]="parseStatusEq(r.parseStatus, 'MalformedJson') ? (r.parseError ?? 'sidecar invalid') : 'no JSON sidecar'">
                  ⚠️ {{ parseStatusEq(r.parseStatus, 'MalformedJson') ? 'malformed' : 'unstructured' }}
                </span>
              }
              <span class="pdov__hist-ts">{{ formatTime(r.createdAt) }}</span>
            </li>
          }
        </ul>
      }

      @if (reportDetail(); as detail) {
        <section class="pdov__report-modal"
                 data-testid="project-drift-overview-report-modal"
                 (click)="closeReport($event)">
          <article class="pdov__report-card" (click)="stop($event)">
            <header>
              <h3>
                Drift report
                <code>{{ detail.report.reportId }}</code>
                <span class="pdov__band pdov__band--{{ bandClass(detail.report.scoreBand) }}">{{ tc(detail.report.scoreBand) }}</span>
              </h3>
              <button class="pdov__close"
                      data-testid="project-drift-overview-report-close"
                      (click)="closeReport()">×</button>
            </header>

            @if (parseStatusEq(detail.report.parseStatus, 'Unstructured')) {
              <div class="pdov__warn" data-testid="project-drift-overview-report-warn-unstructured">
                ⚠️ <strong>Unstructured report.</strong>
                Markdown is the only artifact below.
              </div>
            } @else if (parseStatusEq(detail.report.parseStatus, 'MalformedJson')) {
              <div class="pdov__warn pdov__warn--strong" data-testid="project-drift-overview-report-warn-malformed">
                ⚠️ <strong>Malformed JSON sidecar.</strong>
                <code>{{ detail.report.parseError ?? 'sidecar invalid' }}</code>
              </div>
            }

            <p class="pdov__latest-summary">{{ detail.report.summary }}</p>
            <p class="pdov__latest-meta">
              {{ formatTime(detail.report.createdAt) }}
              · score {{ detail.report.overallScore }} / 100
              · trigger {{ tc(detail.report.trigger) }}
              · scope {{ scopeLabel(detail.report) }}
            </p>

            <h4 class="pdov__panel-sub">Markdown</h4>
            @if (detail.markdown) {
              <pre class="pdov__report-md" data-testid="project-drift-overview-report-md">{{ detail.markdown }}</pre>
            } @else {
              <p class="proj-detail__empty">No Markdown body recorded.</p>
            }
          </article>
        </section>
      }
    </section>
  `,
  styles: [`
    :host { display: block; }
    .pdov__icon { margin-right: 6px; }

    .pdov__band {
      display: inline-block;
      margin-left: 8px;
      padding: 1px 8px;
      border-radius: 999px;
      font-size: 0.72rem;
      font-weight: 600;
      letter-spacing: 0.02em;
      text-transform: uppercase;
      background: rgba(255,255,255,0.06);
      color: #cbd5e1;
    }
    .pdov__band--healthy { background: rgba(166,227,161,0.18); color: #a6e3a1; }
    .pdov__band--watch   { background: rgba(148,226,213,0.16); color: #94e2d5; }
    .pdov__band--warn    { background: rgba(249,226,175,0.20); color: #f9e2af; }
    .pdov__band--critical{ background: rgba(243,139,168,0.22); color: #f38ba8; }
    .pdov__band--unknown { background: rgba(255,255,255,0.06); color: rgba(255,255,255,0.55); }

    .pdov__score-pill {
      display: inline-block;
      margin-left: 6px;
      padding: 1px 8px;
      border-radius: 999px;
      background: rgba(255,255,255,0.06);
      color: #cbd5e1;
      font-size: 0.72rem;
      font-weight: 700;
      font-variant-numeric: tabular-nums;
    }

    .pdov__sub {
      margin: 16px 0 6px;
      font-size: 0.78rem;
      color: rgba(255,255,255,0.65);
      text-transform: uppercase;
      letter-spacing: 0.04em;
    }

    .pdov__error {
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
    .pdov__error strong { color: #f38ba8; }

    .pdov__warn {
      margin: 8px 0 6px;
      padding: 6px 10px;
      border-radius: 6px;
      background: rgba(249,226,175,0.12);
      border: 1px solid rgba(249,226,175,0.36);
      color: #f9e2af;
      font-size: 0.82rem;
    }
    .pdov__warn--strong {
      background: rgba(243,139,168,0.12);
      border-color: rgba(243,139,168,0.40);
      color: #fda4af;
    }
    .pdov__warn code { background: rgba(0,0,0,0.30); padding: 1px 4px; border-radius: 3px; }

    .pdov__actions {
      display: flex; flex-wrap: wrap; gap: 6px;
      margin-bottom: 6px;
    }
    .pdov__btn {
      background: rgba(137,180,250,0.10);
      color: #cdd6f4;
      border: 1px solid rgba(137,180,250,0.35);
      border-radius: 6px;
      padding: 4px 10px;
      font-size: 0.82rem;
      cursor: pointer;
    }
    .pdov__btn:hover:not([disabled]) { background: rgba(137,180,250,0.18); }
    .pdov__btn[disabled] { opacity: 0.55; cursor: progress; }
    .pdov__btn--ghost { background: rgba(255,255,255,0.06); border-color: rgba(255,255,255,0.12); }
    .pdov__btn--accent {
      background: rgba(196,181,253,0.14);
      border-color: rgba(196,181,253,0.45);
      color: #c4b5fd;
    }
    .pdov__btn--accent:hover:not([disabled]) { background: rgba(196,181,253,0.22); }
    .pdov__btn-spin { margin-left: 4px; opacity: 0.8; }

    .pdov__action-msg { margin: 6px 0 0; color: #94e2d5; font-size: 0.78rem; }
    .pdov__action-error { margin: 6px 0 0; color: #fda4af; font-size: 0.78rem; }

    .pdov__latest {
      margin: 14px 0 0;
      padding: 12px;
      border-radius: 8px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.10);
    }
    .pdov__latest-header { display: flex; justify-content: space-between; align-items: flex-start; gap: 12px; }
    .pdov__latest-headline { flex: 1; min-width: 0; }
    .pdov__latest-summary { margin: 0 0 4px; color: #f8fafc; font-size: 0.92rem; line-height: 1.4; }
    .pdov__latest-meta { margin: 0; color: rgba(255,255,255,0.55); font-size: 0.74rem; }
    .pdov__latest-meta code { color: #c4b5fd; background: rgba(0,0,0,0.30); padding: 1px 4px; border-radius: 3px; }

    .pdov__dimensions {
      display: grid;
      grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
      gap: 8px;
      margin: 6px 0;
    }
    .pdov__dim {
      text-align: left;
      padding: 8px 10px;
      border-radius: 8px;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.10);
      color: #cdd6f4;
      cursor: pointer;
      display: flex; flex-direction: column; gap: 4px;
      min-height: 92px;
    }
    .pdov__dim:hover:not(.pdov__dim--empty) { background: rgba(255,255,255,0.06); border-color: rgba(255,255,255,0.22); }
    .pdov__dim header { display: flex; justify-content: space-between; align-items: baseline; gap: 8px; }
    .pdov__dim-label { font-weight: 600; color: #f8fafc; font-size: 0.86rem; }
    .pdov__dim-score { font-variant-numeric: tabular-nums; font-weight: 700; font-size: 1rem; color: #cbd5e1; }
    .pdov__dim-summary { margin: 0; color: rgba(205,214,244,0.75); font-size: 0.76rem; line-height: 1.4; flex-grow: 1; }
    .pdov__dim-summary--muted { color: rgba(255,255,255,0.4); font-style: italic; }
    .pdov__dim footer { display: flex; align-items: center; gap: 6px; flex-wrap: wrap; font-size: 0.72rem; }
    .pdov__dim--info     { border-left: 3px solid rgba(148,163,184,0.55); }
    .pdov__dim--warn     { border-left: 3px solid #f9e2af; }
    .pdov__dim--high     { border-left: 3px solid #fab387; background: rgba(250,179,135,0.06); }
    .pdov__dim--critical { border-left: 3px solid #f38ba8; background: rgba(243,139,168,0.10); }
    .pdov__dim--empty    { cursor: default; opacity: 0.7; }

    .pdov__sev {
      text-transform: uppercase;
      font-weight: 600;
      padding: 1px 6px;
      border-radius: 3px;
      background: rgba(255,255,255,0.08);
    }
    .pdov__sev--info { background: rgba(148,163,184,0.18); color: #cbd5e1; }
    .pdov__sev--warn { background: rgba(249,226,175,0.20); color: #f9e2af; }
    .pdov__sev--high { background: rgba(250,179,135,0.20); color: #fab387; }
    .pdov__sev--critical { background: rgba(243,139,168,0.22); color: #f38ba8; }

    .pdov__cov { color: rgba(255,255,255,0.55); font-variant-numeric: tabular-nums; }
    .pdov__status {
      padding: 1px 6px;
      border-radius: 3px;
      font-weight: 600;
      letter-spacing: 0.02em;
      background: rgba(255,255,255,0.06);
      color: #cbd5e1;
    }
    .pdov__status--accepted { background: rgba(166,227,161,0.15); color: #a6e3a1; }
    .pdov__status--tracked  { background: rgba(137,180,250,0.18); color: #89b4fa; }
    .pdov__status--ignored  { background: rgba(255,255,255,0.06); color: rgba(255,255,255,0.55); text-decoration: line-through; }
    .pdov__status--resolved { background: rgba(148,226,213,0.15); color: #94e2d5; }

    .pdov__panel {
      margin-top: 12px;
      padding: 12px;
      background: rgba(0,0,0,0.30);
      border: 1px solid rgba(196,181,253,0.30);
      border-radius: 6px;
    }
    .pdov__panel header {
      display: flex; align-items: baseline; justify-content: space-between;
      margin-bottom: 6px;
    }
    .pdov__panel header h4 { margin: 0; color: #f8fafc; font-size: 0.95rem; }
    .pdov__panel-score { color: #cbd5e1; font-variant-numeric: tabular-nums; font-size: 0.85rem; margin-left: 8px; }
    .pdov__panel-sub { margin: 8px 0 4px; font-size: 0.78rem; color: rgba(255,255,255,0.65); }
    .pdov__panel-summary { margin: 0 0 8px; color: #e2e8f0; font-size: 0.86rem; line-height: 1.5; }

    .pdov__close {
      background: transparent; border: none;
      color: rgba(255,255,255,0.55);
      cursor: pointer; font-size: 1.1rem; line-height: 1;
    }
    .pdov__close:hover { color: #f8fafc; }

    .pdov__panel-meta {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 4px 12px;
      margin: 0 0 8px;
      font-size: 0.82rem;
    }
    .pdov__panel-meta > div { display: contents; }
    .pdov__panel-meta dt { color: rgba(255,255,255,0.55); }
    .pdov__panel-meta dd { margin: 0; color: #cdd6f4; }

    .pdov__panel-details { margin: 6px 0; }
    .pdov__panel-details summary { cursor: pointer; color: rgba(255,255,255,0.65); font-size: 0.80rem; user-select: none; }
    .pdov__panel-details ul { margin: 6px 0 0; padding-left: 20px; font-size: 0.82rem; color: #cdd6f4; }
    .pdov__panel-details code { background: rgba(255,255,255,0.06); padding: 1px 4px; border-radius: 3px; font-size: 0.78rem; color: #c4b5fd; }

    .pdov__findings { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 8px; }
    .pdov__finding {
      padding: 8px 10px;
      border-left: 2px solid rgba(255,255,255,0.10);
      background: rgba(255,255,255,0.03);
      border-radius: 0 4px 4px 0;
    }
    .pdov__finding--info { border-left-color: rgba(148,163,184,0.45); }
    .pdov__finding--warn { border-left-color: #f9e2af; }
    .pdov__finding--high { border-left-color: #fab387; background: rgba(250,179,135,0.06); }
    .pdov__finding--critical { border-left-color: #f38ba8; background: rgba(243,139,168,0.10); }
    .pdov__finding header { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; margin-bottom: 4px; }
    .pdov__finding p { margin: 0 0 6px; color: #cdd6f4; font-size: 0.84rem; line-height: 1.45; }
    .pdov__finding-id code { background: rgba(255,255,255,0.06); padding: 1px 4px; border-radius: 3px; font-size: 0.74rem; color: #c4b5fd; }
    .pdov__finding-actions { display: flex; gap: 6px; margin-top: 6px; }

    .pdov__followups { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 8px; }
    .pdov__followup {
      padding: 8px 10px;
      border-radius: 6px;
      background: rgba(255,255,255,0.03);
      border-left: 2px solid rgba(196,181,253,0.40);
    }
    .pdov__followup header { display: flex; flex-wrap: wrap; align-items: baseline; gap: 8px; margin-bottom: 4px; }
    .pdov__followup p { margin: 0 0 6px; color: #cdd6f4; font-size: 0.84rem; line-height: 1.45; }
    .pdov__prio {
      text-transform: uppercase;
      font-size: 0.70rem;
      padding: 1px 6px;
      border-radius: 3px;
      background: rgba(255,255,255,0.08);
      color: #cbd5e1;
    }
    .pdov__prio--low { background: rgba(148,163,184,0.18); color: #cbd5e1; }
    .pdov__prio--normal { background: rgba(137,180,250,0.18); color: #89b4fa; }
    .pdov__prio--high { background: rgba(250,179,135,0.20); color: #fab387; }
    .pdov__prio--critical { background: rgba(243,139,168,0.22); color: #f38ba8; }
    .pdov__rel { font-size: 0.72rem; color: rgba(255,255,255,0.55); }
    .pdov__followup-job { font-size: 0.74rem; color: rgba(148,226,213,0.85); margin-left: auto; }
    .pdov__followup-job code { background: rgba(0,0,0,0.30); padding: 1px 4px; border-radius: 3px; }

    .pdov__history { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 4px; }
    .pdov__hist-row {
      display: grid;
      grid-template-columns: max-content max-content 1fr max-content max-content max-content;
      gap: 10px;
      align-items: center;
      padding: 6px 8px;
      border-radius: 4px;
      cursor: pointer;
      background: rgba(255,255,255,0.03);
      border: 1px solid rgba(255,255,255,0.08);
      font-size: 0.80rem;
    }
    .pdov__hist-row:hover { background: rgba(255,255,255,0.07); border-color: rgba(255,255,255,0.20); }
    .pdov__hist-band {
      padding: 1px 6px;
      border-radius: 3px;
      font-size: 0.70rem;
      font-weight: 600;
      letter-spacing: 0.02em;
      text-transform: uppercase;
    }
    .pdov__hist-band--healthy  { background: rgba(166,227,161,0.18); color: #a6e3a1; }
    .pdov__hist-band--watch    { background: rgba(148,226,213,0.16); color: #94e2d5; }
    .pdov__hist-band--warn     { background: rgba(249,226,175,0.20); color: #f9e2af; }
    .pdov__hist-band--critical { background: rgba(243,139,168,0.22); color: #f38ba8; }
    .pdov__hist-band--unknown  { background: rgba(255,255,255,0.06); color: rgba(255,255,255,0.55); }
    .pdov__hist-score { font-variant-numeric: tabular-nums; color: #cdd6f4; min-width: 28px; text-align: right; }
    .pdov__hist-summary { color: #cdd6f4; min-width: 0; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
    .pdov__hist-trigger { color: rgba(255,255,255,0.55); font-size: 0.72rem; padding: 1px 5px; background: rgba(255,255,255,0.05); border-radius: 3px; }
    .pdov__hist-parse { color: #f9e2af; font-size: 0.72rem; }
    .pdov__hist-ts { color: rgba(255,255,255,0.50); font-variant-numeric: tabular-nums; font-size: 0.72rem; }

    .pdov__report-modal {
      position: fixed;
      inset: 0;
      background: rgba(0,0,0,0.55);
      display: flex; align-items: center; justify-content: center;
      z-index: 80;
      padding: 24px;
    }
    .pdov__report-card {
      background: #181825;
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 10px;
      padding: 14px 16px;
      width: min(840px, 100%);
      max-height: calc(100vh - 48px);
      overflow: auto;
    }
    .pdov__report-card header { display: flex; align-items: center; justify-content: space-between; margin-bottom: 8px; }
    .pdov__report-card header h3 { margin: 0; color: #f8fafc; font-size: 1rem; display: flex; align-items: baseline; gap: 8px; }
    .pdov__report-card header h3 code { background: rgba(255,255,255,0.06); padding: 1px 6px; border-radius: 3px; color: #c4b5fd; font-size: 0.85rem; }
    .pdov__report-md {
      max-height: 480px;
      overflow: auto;
      background: rgba(0,0,0,0.30);
      border: 1px solid rgba(255,255,255,0.10);
      border-radius: 6px;
      padding: 10px 12px;
      font-size: 0.80rem;
      color: #cdd6f4;
      white-space: pre-wrap;
    }
  `]
})
export class ProjectDriftOverviewSectionComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();

  private readonly drift = inject(DriftService);
  private readonly jobs = inject(JobService);

  readonly DIMENSION_LABELS = DIMENSION_LABELS;
  readonly actions = ACTIONS;

  readonly reports = signal<DriftReport[]>([]);
  readonly loading = signal<boolean>(false);
  readonly error = signal<string | null>(null);
  readonly busyAction = signal<string | null>(null);
  readonly actionMessage = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);
  readonly busyFollowUp = signal<string | null>(null);
  readonly openedDimensionType = signal<DriftDimensionType | null>(null);
  readonly reportDetail = signal<DriftReportDetailResponse | null>(null);

  readonly latest = computed<DriftReport | null>(() => {
    const list = this.reports();
    return list.length > 0 ? list[0] : null;
  });

  readonly dimensionRows = computed<DimensionCardRow[]>(() => {
    const r = this.latest();
    const byType = new Map<DriftDimensionType, DriftDimension>();
    if (r) for (const d of r.dimensions) {
      const normalized = normalizeDimensionType(d.type);
      if (normalized) byType.set(normalized, { ...d, type: normalized });
    }
    return ALL_DIMENSIONS.map(t => ({
      type: t,
      label: DIMENSION_LABELS[t],
      dimension: byType.get(t) ?? null,
    }));
  });

  readonly openedDimension = computed<DimensionCardRow | null>(() => {
    const t = this.openedDimensionType();
    if (!t) return null;
    return this.dimensionRows().find(r => r.type === t && r.dimension) ?? null;
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
    this.drift.listReports(project, { limit: 50 }).subscribe({
      next: (resp) => {
        try {
          const list = Array.isArray(resp?.reports) ? resp.reports : [];
          this.reports.set(list);
          this.error.set(null);
        } catch (e) {
          this.error.set(this.describe(e, 'Could not parse drift report list.'));
        }
        this.loading.set(false);
      },
      error: (err) => {
        this.error.set(this.describe(err, 'Drift API call failed.'));
        this.loading.set(false);
      },
    });
  }

  toggleDimension(type: DriftDimensionType): void {
    this.openedDimensionType.update(curr => curr === type ? null : type);
  }

  closeDimension(): void {
    this.openedDimensionType.set(null);
  }

  openReport(reportId: string): void {
    const project = this.projectName();
    if (!project || !reportId) return;
    this.drift.getReport(project, reportId).subscribe({
      next: (resp) => this.reportDetail.set(resp ?? null),
      error: (err) => this.actionError.set(this.describe(err, 'Could not open drift report.')),
    });
  }

  closeReport(ev?: Event): void {
    if (ev && ev.target && (ev.target as HTMLElement).closest('.pdov__report-card')) return;
    this.reportDetail.set(null);
  }

  stop(ev: Event): void { ev.stopPropagation(); }

  runAction(action: ActionButton): void {
    const project = this.projectName();
    if (!project) return;
    this.busyAction.set(action.slug);
    this.actionMessage.set(null);
    this.actionError.set(null);
    if (action.kind === 'inline' && action.slug === 'adrs-code') {
      this.drift.runAdrCodeDrift(project).subscribe({
        next: (resp) => {
          this.busyAction.set(null);
          this.actionMessage.set(`ADR / Code drift evidence captured (report ${resp?.report?.reportId ?? '…'}).`);
          this.refresh(true);
        },
        error: (err) => {
          this.busyAction.set(null);
          this.actionError.set(this.describe(err, 'ADR / Code drift action failed.'));
        },
      });
      return;
    }
    if (action.kind === 'inline' && action.slug === 'docs-marketing') {
      this.drift.runDocsMarketingDrift(project).subscribe({
        next: (resp) => {
          this.busyAction.set(null);
          this.actionMessage.set(`Docs / Marketing drift evidence captured (report ${resp?.report?.reportId ?? '…'}).`);
          this.refresh(true);
        },
        error: (err) => {
          this.busyAction.set(null);
          this.actionError.set(this.describe(err, 'Docs / Marketing drift action failed.'));
        },
      });
      return;
    }
    // queue: create a 1-preparation task with a templated drift-analysis prompt.
    this.queueDriftAnalysisTask(project, action);
  }

  private queueDriftAnalysisTask(project: string, action: ActionButton): void {
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busyAction.set(null);
          this.actionError.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const slug = `drift-${action.slug}-${Date.now().toString(36)}`;
        const promptMarkdown = this.buildDriftPrompt(action);
        this.jobs.createJob({
          id: slug,
          title: action.label,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: '1-preparation',
        }).subscribe({
          next: (resp) => {
            this.busyAction.set(null);
            this.actionMessage.set(`Queued ${resp?.id ?? slug} in 1-preparation. Promote to 2-ready when scoped.`);
          },
          error: (err) => {
            this.busyAction.set(null);
            this.actionError.set(this.describe(err, 'Could not queue drift analysis task.'));
          },
        });
      },
      error: () => {
        this.busyAction.set(null);
        this.actionError.set('Could not resolve watch paths.');
      },
    });
  }

  createFollowUpFromFinding(dim: DriftDimension, finding: DriftFinding): void {
    const project = this.projectName();
    if (!project) return;
    this.busyFollowUp.set(finding.findingId);
    this.actionMessage.set(null);
    this.actionError.set(null);
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busyFollowUp.set(null);
          this.actionError.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const slug = `followup-drift-${dimSlugStatic(dim.type)}-${finding.findingId.slice(0, 16)}-${Date.now().toString(36)}`.slice(0, 100);
        const title = `Follow-up: ${DIMENSION_LABELS[dim.type]} drift - ${truncate(finding.summary, 60)}`;
        const promptMarkdown = this.buildFindingFollowUpPrompt(dim, finding);
        this.jobs.createJob({
          id: slug,
          title,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: '1-preparation',
        }).subscribe({
          next: (resp) => {
            this.busyFollowUp.set(null);
            this.actionMessage.set(`Queued follow-up task ${resp?.id ?? slug} in 1-preparation.`);
          },
          error: (err) => {
            this.busyFollowUp.set(null);
            this.actionError.set(this.describe(err, 'Could not queue follow-up task.'));
          },
        });
      },
      error: () => {
        this.busyFollowUp.set(null);
        this.actionError.set('Could not resolve watch paths.');
      },
    });
  }

  createFollowUpFromSuggestion(s: DriftFollowUpSuggestion): void {
    const project = this.projectName();
    if (!project) return;
    this.busyFollowUp.set(s.title);
    this.actionMessage.set(null);
    this.actionError.set(null);
    this.resolveWatchPath(project).subscribe({
      next: (watchPath) => {
        if (!watchPath) {
          this.busyFollowUp.set(null);
          this.actionError.set(`Could not resolve watchPath for project "${project}".`);
          return;
        }
        const newSlug = `followup-drift-${toSlug(s.title).slice(0, 40)}-${Date.now().toString(36)}`;
        const title = truncate(s.title, 90);
        const promptMarkdown = this.buildSuggestionFollowUpPrompt(s);
        this.jobs.createJob({
          id: newSlug,
          title,
          agent: 'claude',
          watchPath,
          promptMarkdown,
          targetState: (s.targetState === '2-ready' ? '2-ready' : '1-preparation'),
        }).subscribe({
          next: (resp) => {
            this.busyFollowUp.set(null);
            this.actionMessage.set(`Queued follow-up task ${resp?.id ?? newSlug}.`);
          },
          error: (err) => {
            this.busyFollowUp.set(null);
            this.actionError.set(this.describe(err, 'Could not queue follow-up task.'));
          },
        });
      },
      error: () => {
        this.busyFollowUp.set(null);
        this.actionError.set('Could not resolve watch paths.');
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

  private buildDriftPrompt(action: ActionButton): string {
    return `# ${action.label}

Spawned from the project Drift overview surface.

## What to do

${action.description}

Produce a Markdown report plus an inline JSON block conforming to
\`docs/schemas/drift-report.schema.json\`. POST the reply back through the
appropriate \`/api/drift/{project}/actions/...\` endpoint when one exists,
or attach the report to this task's \`status.md\` for review.

The orchestrator must not move source state on the strength of this
report; produce evidence only.
`;
  }

  private buildFindingFollowUpPrompt(dim: DriftDimension, finding: DriftFinding): string {
    const refs = (finding.evidenceRefs ?? []).map(r => `- \`${r}\``).join('\n');
    return `# Drift follow-up: ${DIMENSION_LABELS[dim.type]} - ${truncate(finding.summary, 80)}

Created from the project Drift overview.

**Dimension:** ${dim.type}
**Finding id:** ${finding.findingId}
**Severity:** ${finding.severity}
**Status:** ${finding.status}

## Finding

> ${finding.summary}

## Evidence
${refs || '_(no evidence refs on the finding)_'}

## What to do

Confirm the drift, list affected files, and decide whether the work belongs
in a single task or several. Promote to \`2-ready\` only after the scope is
clear and the change is bounded.
`;
  }

  private buildSuggestionFollowUpPrompt(s: DriftFollowUpSuggestion): string {
    return `# Drift follow-up: ${s.title}

Created from the project Drift overview suggestions.

**Priority:** ${s.priority}
${s.relatedDimension ? `**Related dimension:** ${s.relatedDimension}\n` : ''}

## Summary

${s.summary ?? '_(no summary on suggestion)_'}

## What to do

Refine and bound this scope, then promote when ready. Drift is a triage
signal; do not let the suggestion text replace evidence-based scoping.
`;
  }

  // ----------------------------------------------------------------------
  // Display helpers
  // ----------------------------------------------------------------------

  /** Display helper: render an enum-style string with a leading capital,
   *  regardless of wire case. The backend serializes drift enums lowercase
   *  ('warn', 'high', 'manual', 'schema') even though the schema and code
   *  are PascalCase; the UI needs a single normalized presentation. */
  tc(value: string | null | undefined): string { return titleCase(value); }

  /** Case-insensitive parseStatus equality. Wire is lowercase ('structured'),
   *  comparisons in templates target the PascalCase enum names. */
  parseStatusEq(actual: string | null | undefined, expected: string): boolean {
    return (actual ?? '').toString().toLowerCase() === expected.toLowerCase();
  }

  bandClass(b: DriftScoreBand | string | undefined | null): string {
    return (b ?? 'unknown').toString().toLowerCase();
  }

  severityClass(s: DriftSeverity | string | undefined | null): string {
    return (s ?? 'info').toString().toLowerCase();
  }

  statusClass(s: string | undefined | null): string {
    return (s ?? 'new').toString().toLowerCase();
  }

  priorityClass(p: string | undefined | null): string {
    return (p ?? 'normal').toString().toLowerCase();
  }

  dimSlug(t: DriftDimensionType): string { return dimSlugStatic(t); }

  slug(s: string): string { return toSlug(s); }

  formatCoverage(c: number | undefined | null): string {
    if (typeof c !== 'number' || Number.isNaN(c)) return '—';
    return `${Math.round(c * 100)}%`;
  }

  formatConfidence(c: number | undefined | null): string {
    if (typeof c !== 'number' || Number.isNaN(c)) return '—';
    return `${Math.round(c * 100)}%`;
  }

  formatTime(iso: string | undefined | null): string {
    if (!iso) return '';
    try {
      const d = new Date(iso);
      if (Number.isNaN(d.getTime())) return iso;
      return d.toLocaleString();
    } catch {
      return iso;
    }
  }

  scopeLabel(r: DriftReport): string {
    const k = r.scope?.kind ?? 'Project';
    if (k === 'Task' && r.scope.taskId) return `task / ${r.scope.taskId}`;
    if (k === 'Run' && r.scope.taskId) return `run / ${r.scope.taskId}#${r.scope.runIndex ?? '?'}`;
    if (k === 'Workspace') return 'workspace';
    if (k === 'TimeWindow') return 'time-window';
    return 'project';
  }

  private describe(err: unknown, fallback: string): string {
    if (!err) return fallback;
    const e = err as { error?: { error?: string }; message?: string };
    return e.error?.error ?? e.message ?? fallback;
  }
}

/**
 * Backend serializes enum values lowercase ('schema', 'taskJob'). The
 * dimension lookup map uses our PascalCase canonical keys, so each
 * received type is normalized before insertion. Unknown keys return
 * null and the dimension is dropped from the projection.
 */
function normalizeDimensionType(raw: string | null | undefined): DriftDimensionType | null {
  if (!raw) return null;
  const lower = raw.toString().toLowerCase().replace(/[^a-z]/g, '');
  switch (lower) {
    case 'intent':       return 'Intent';
    case 'spec':         return 'Spec';
    case 'taskjob':      return 'TaskJob';
    case 'architecture': return 'Architecture';
    case 'documentation':return 'Documentation';
    case 'marketing':    return 'Marketing';
    case 'design':       return 'Design';
    case 'test':         return 'Test';
    case 'runtime':      return 'Runtime';
    case 'process':      return 'Process';
    case 'schema':       return 'Schema';
    case 'token':        return 'Token';
    default:             return null;
  }
}

function titleCase(s: string | null | undefined): string {
  if (!s) return '';
  const str = s.toString();
  return str.charAt(0).toUpperCase() + str.slice(1).toLowerCase();
}

function dimSlugStatic(t: DriftDimensionType): string {
  // 'TaskJob' -> 'task-job'; lowercase the rest.
  switch (t) {
    case 'TaskJob': return 'task-job';
    default: return t.toLowerCase();
  }
}

function toSlug(s: string): string {
  return s.toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
}

function truncate(s: string, max: number): string {
  if (!s) return '';
  return s.length > max ? s.slice(0, max - 1) + '…' : s;
}
