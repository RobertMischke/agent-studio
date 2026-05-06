import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobService } from '../services/job.service';
import { GroupedJobs, OrchestratorLogEntry, OrchestratorSession, RunnerStatus } from '../models/job.model';
import { OrchestratorRunner_KnownModels } from './project-detail.models';
import { TokenSummaryBlockComponent } from './token-summary-block';
import { GlobalOrchestratorCardComponent } from './global-orchestrator-card';
import { ProjectSecuritySectionComponent } from './project-security-section';
import { ProjectArchitectureSectionComponent } from './project-architecture-section';
import { ProjectDriftSectionComponent } from './project-drift-section';
import { ProjectDriftOverviewSectionComponent } from './project-drift-overview-section';
import { ProjectSupervisorSectionComponent } from './project-supervisor-section';
import { ProjectMetaCycleSectionComponent } from './project-meta-cycle-section';
import { ProjectAnalysisReportsSectionComponent } from './project-analysis-reports-section';
import { ProjectSteeringDocsSectionComponent } from './project-steering-docs-section';
import { AutonomySliderComponent } from './autonomy-slider';
import { AnalysisReport } from '../models/analysis-report.model';

interface ProjectSettingsRow {
  autoCommit: boolean;
  runnerMode: string | null;
  orchestratorModel: string | null;
}

/**
 * Project detail panel: name + paths, runner mode toggle, orchestrator
 * model selector, auto-commit toggle, job-state counts, the most recent
 * orchestrator entries with token totals, and a button to open the full
 * feed. Mounted as an overlay panel from the project-tabs ⚙ button.
 *
 * Read-mostly: only the three setting controls write back. Everything
 * else is polled (5s interval) so a backend change made elsewhere is
 * reflected without manual refresh.
 */
@Component({
  selector: 'app-project-detail',
  standalone: true,
  imports: [
    FormsModule,
    TokenSummaryBlockComponent,
    GlobalOrchestratorCardComponent,
    ProjectSecuritySectionComponent,
    ProjectArchitectureSectionComponent,
    ProjectDriftSectionComponent,
    ProjectDriftOverviewSectionComponent,
    ProjectSupervisorSectionComponent,
    ProjectMetaCycleSectionComponent,
    ProjectAnalysisReportsSectionComponent,
    ProjectSteeringDocsSectionComponent,
    AutonomySliderComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="proj-detail" data-testid="project-detail">
      <header class="proj-detail__head">
        <h2 class="proj-detail__title">{{ projectName() }}</h2>
        <button class="proj-detail__feed" (click)="openFeed.emit(projectName())" data-testid="project-detail-open-feed">
          📜 Open feed
        </button>
      </header>

      <app-global-orchestrator-card />

      @if (pendingDecisions().length > 0) {
        <section class="proj-detail__banner" data-testid="project-detail-needs-input-banner">
          <header>
            <span class="proj-detail__banner-icon">⚠️</span>
            <strong>Orchestrator decision pending</strong>
            <span class="proj-detail__banner-count">{{ pendingDecisions().length }} task{{ pendingDecisions().length === 1 ? '' : 's' }}</span>
          </header>
          <ul>
            @for (p of pendingDecisions(); track p.jobId) {
              <li>
                <code>{{ p.jobId }}</code>
                <span class="proj-detail__banner-reason">{{ p.reason || '(no reason given)' }}</span>
              </li>
            }
          </ul>
          <p class="proj-detail__hint">
            One or more 4-auto-review tasks ended in <code>[[TASK_NEEDS_INPUT]]</code>. The orchestrator will pick them up on the next tick (≈ 30 s) and either reissue, accept-as-done (promotes to 5-human-review), or escalate (also to 5-human-review).
          </p>
        </section>
      }

      @if (livePendingDecisions().length > 0) {
        @for (p of livePendingDecisions(); track p.jobId) {
          <section class="proj-detail__live-banner"
                   [attr.data-testid]="'project-detail-live-decision-banner-' + p.jobId"
                   [class.proj-detail__live-banner--blocked]="p.kind === 'blocked'">
            <header>
              <span class="proj-detail__live-banner__icon" aria-hidden="true">{{ p.kind === 'blocked' ? '⛔' : '🛎️' }}</span>
              <strong>{{ p.kind === 'blocked' ? 'Agent blocked' : 'Agent is asking for input' }}</strong>
              <span class="proj-detail__live-banner__chip">live · {{ p.jobId }}</span>
            </header>
            <p class="proj-detail__live-banner__title">{{ p.title }}</p>
            <p class="proj-detail__live-banner__reason">{{ p.reason || '(no reason given by the agent)' }}</p>
            <div class="proj-detail__live-banner__reply">
              <textarea
                [attr.data-testid]="'project-detail-live-decision-reply-' + p.jobId"
                [(ngModel)]="liveReplyDrafts[p.jobId]"
                rows="3"
                placeholder="Reply to the agent. This goes through the existing continue endpoint (mode: steer) and resolves the banner once received."></textarea>
              <div class="proj-detail__live-banner__actions">
                @if (liveReplyErrors[p.jobId]) {
                  <span class="proj-detail__live-banner__error">{{ liveReplyErrors[p.jobId] }}</span>
                }
                <button type="button"
                        class="proj-detail__live-banner__send"
                        [attr.data-testid]="'project-detail-live-decision-send-' + p.jobId"
                        [disabled]="liveReplySending[p.jobId] || !(liveReplyDrafts[p.jobId] || '').trim()"
                        (click)="sendLiveDecisionReply(p.jobId)">
                  {{ liveReplySending[p.jobId] ? 'Sending…' : 'Reply' }}
                </button>
              </div>
            </div>
          </section>
        }
      }

      @if (paths(); as p) {
        <dl class="proj-detail__paths">
          <div><dt>Watch path</dt><dd>{{ p.path }}</dd></div>
          <div><dt>Working directory</dt><dd>{{ p.rootPath || '(same as watch path)' }}</dd></div>
          <div><dt>Repository</dt><dd>{{ p.repositoryPath || '(none configured)' }}</dd></div>
        </dl>
      }

      <section class="proj-detail__group">
        <h3>Runner mode</h3>
        <div class="proj-detail__modes">
          @for (m of modes; track m.id) {
            <button class="proj-detail__mode"
                    [class.proj-detail__mode--active]="effectiveMode() === m.id"
                    [attr.data-testid]="'project-detail-mode-' + m.id"
                    [title]="m.tooltip"
                    (click)="setMode(m.id)">{{ m.label }}</button>
          }
        </div>
        <p class="proj-detail__hint">{{ modeHint() }}</p>
      </section>

      <section class="proj-detail__group">
        <h3>Orchestrator model</h3>
        <div class="proj-detail__model-row">
          <select class="proj-detail__model-select"
                  data-testid="project-detail-orch-model"
                  [(ngModel)]="orchModelDraft"
                  (ngModelChange)="onOrchModelChange()">
            @for (opt of orchModelOptions; track opt.id) {
              <option [value]="opt.id">{{ opt.label }}</option>
            }
          </select>
          <span class="proj-detail__hint proj-detail__hint--inline">
            Used when the orchestrator decides on your behalf in auto mode.
          </span>
        </div>
      </section>

      <section class="proj-detail__group">
        <app-autonomy-slider [projectName]="projectName()" />
      </section>

      <section class="proj-detail__group">
        <h3>Auto-commit</h3>
        <label class="proj-detail__toggle">
          <input type="checkbox"
                 data-testid="project-detail-auto-commit"
                 [(ngModel)]="autoCommitDraft"
                 (ngModelChange)="onAutoCommitChange()">
          <span>Auto-commit on transition <code>3-progress → 4-auto-review</code></span>
        </label>
      </section>

      <section class="proj-detail__group">
        <h3>Lane counts</h3>
        <div class="proj-detail__counts">
          @for (lane of laneCounts(); track lane.state) {
            <div class="proj-detail__count">
              <span class="proj-detail__count-num">{{ lane.count }}</span>
              <span class="proj-detail__count-state">{{ lane.label }}</span>
            </div>
          }
        </div>
      </section>

      <section class="proj-detail__group">
        <h3>Orchestrator session</h3>
        @if (orchSession(); as os) {
          <dl class="proj-detail__paths proj-detail__session">
            <div><dt>Status</dt><dd>● Live · model {{ os.model }}</dd></div>
            <div><dt>Session id</dt><dd><code>{{ os.sessionId }}</code></dd></div>
            <div><dt>Booted</dt><dd>{{ formatTime(os.bootedAt) }} · {{ os.calls }} call{{ os.calls === 1 ? '' : 's' }} so far</dd></div>
            @if (os.lastError) {
              <div><dt>Last error</dt><dd class="proj-detail__session-error">{{ os.lastError }}</dd></div>
            }
          </dl>
          <details class="proj-detail__session-snap">
            <summary>What the orchestrator read on boot</summary>
            <pre class="proj-detail__session-pre">{{ os.bootPromptPreview }}</pre>
          </details>
          <details class="proj-detail__session-snap">
            <summary>Boot reply ("I am ready ...")</summary>
            <pre class="proj-detail__session-pre">{{ os.bootReplyPreview }}</pre>
          </details>
          <p class="proj-detail__hint">
            Resume this session yourself with <code>claude -r {{ os.sessionId }}</code> from <code>{{ paths().path }}</code>.
          </p>
        } @else {
          <p class="proj-detail__empty">
            No session booted yet. The orchestrator boots one Claude session per project at app start; if this stays empty after a few seconds, the boot probably failed (check the API log) and decisions will fall back to one-shot calls.
          </p>
        }
      </section>

      <app-token-summary-block [projectName]="projectName()" />

      <app-project-supervisor-section [projectName]="projectName()" />

      <app-project-meta-cycle-section [projectName]="projectName()" />

      <app-project-analysis-reports-section
        [projectName]="projectName()"
        (openReport)="openReport.emit($event)" />

      <app-project-steering-docs-section [projectName]="projectName()" />

      <app-project-security-section [projectName]="projectName()" />

      <app-project-architecture-section [projectName]="projectName()" />

      <app-project-drift-overview-section [projectName]="projectName()" />

      <app-project-drift-section [projectName]="projectName()" />

      <section class="proj-detail__group">
        <h3>Recent orchestrator activity</h3>
        @if (recentEntries().length === 0) {
          <p class="proj-detail__empty">No activity yet for this project.</p>
        } @else {
          <ul class="proj-detail__entries">
            @for (entry of recentEntries(); track entry.ts) {
              <li class="proj-detail__entry"
                  [class.proj-detail__entry--decision]="entry.kind === 'decision'"
                  [class.proj-detail__entry--action]="entry.kind === 'action'"
                  [class.proj-detail__entry--observation]="entry.kind === 'observation'"
                  [class.proj-detail__entry--intervention]="entry.kind === 'intervention'">
                <p>{{ entry.summary }}</p>
                <header>
                  <span class="proj-detail__entry-kind">{{ entry.kind }}</span>
                  <span class="proj-detail__entry-topic">{{ entry.topic }}</span>
                  <span class="proj-detail__entry-ts">{{ formatTime(entry.ts) }}</span>
                </header>
              </li>
            }
          </ul>
        }
      </section>
    </section>
  `,
  styles: [`
    :host { display: block; padding: 18px 20px; max-width: 760px; margin: 0 auto; }
    .proj-detail__head {
      display: flex;
      align-items: baseline;
      gap: 12px;
      margin-bottom: 12px;
      padding-bottom: 8px;
      border-bottom: 1px solid rgba(255,255,255,0.10);
    }
    .proj-detail__title { margin: 0; color: #f8fafc; font-size: 1.1rem; }
    .proj-detail__feed {
      margin-left: auto;
      background: rgba(249, 226, 175, 0.14);
      color: #fcd34d;
      border: 1px solid rgba(249, 226, 175, 0.40);
      border-radius: 6px;
      padding: 4px 10px;
      font-size: 0.78rem;
      cursor: pointer;
    }
    .proj-detail__feed:hover { background: rgba(249, 226, 175, 0.22); }

    .proj-detail__banner {
      margin: 0 0 16px;
      padding: 10px 12px;
      border: 1px solid rgba(249, 226, 175, 0.40);
      border-left-width: 3px;
      background: rgba(249, 226, 175, 0.10);
      border-radius: 6px;
      color: #f1f5f9;
      font-size: 0.85rem;
    }
    .proj-detail__banner header {
      display: flex;
      align-items: baseline;
      gap: 8px;
      margin-bottom: 6px;
    }
    .proj-detail__banner header strong { color: #fcd34d; }
    .proj-detail__banner-icon { font-size: 0.95rem; }
    .proj-detail__banner-count {
      margin-left: auto;
      color: rgba(255,255,255,0.55);
      font-size: 0.75rem;
    }
    .proj-detail__banner ul { margin: 4px 0 0; padding-left: 18px; }
    .proj-detail__banner li { margin: 2px 0; }
    .proj-detail__banner code {
      font-size: 0.78rem;
      background: rgba(255,255,255,0.06);
      padding: 1px 4px;
      border-radius: 3px;
    }
    .proj-detail__banner-reason { color: rgba(255,255,255,0.70); margin-left: 6px; }

    /*
     * ADR-0027: live, in-progress decision banner. Distinct from the
     * yellow post-run "review-decisions-pending" banner above on
     * purpose: this one fires while the agent is still running and is
     * actively asking. Red border + softer red fill so it pops next to
     * the rest of the panel without competing with the yellow review
     * surface. Different shape: a rounded card with a textarea-and-send
     * affordance instead of a list of one-liners.
     */
    .proj-detail__live-banner {
      margin: 0 0 16px;
      padding: 14px 16px;
      border: 1px solid rgba(248, 113, 113, 0.55);
      border-left-width: 4px;
      border-radius: 10px;
      background: linear-gradient(180deg, rgba(248,113,113,0.18) 0%, rgba(248,113,113,0.08) 100%);
      box-shadow: 0 6px 18px rgba(248,113,113,0.12);
      color: #f8fafc;
      font-size: 0.88rem;
    }
    .proj-detail__live-banner--blocked {
      border-color: rgba(244, 114, 182, 0.55);
      background: linear-gradient(180deg, rgba(244,114,182,0.18) 0%, rgba(244,114,182,0.08) 100%);
      box-shadow: 0 6px 18px rgba(244,114,182,0.12);
    }
    .proj-detail__live-banner header {
      display: flex;
      align-items: baseline;
      gap: 10px;
      margin-bottom: 6px;
    }
    .proj-detail__live-banner header strong {
      color: #fda4af;
      font-size: 0.95rem;
      letter-spacing: 0.01em;
    }
    .proj-detail__live-banner--blocked header strong { color: #f9a8d4; }
    .proj-detail__live-banner__icon { font-size: 1.05rem; }
    .proj-detail__live-banner__chip {
      margin-left: auto;
      padding: 2px 8px;
      border-radius: 999px;
      background: rgba(248,113,113,0.20);
      color: #fda4af;
      font-size: 0.72rem;
      letter-spacing: 0.02em;
      text-transform: uppercase;
    }
    .proj-detail__live-banner--blocked .proj-detail__live-banner__chip {
      background: rgba(244,114,182,0.20);
      color: #f9a8d4;
    }
    .proj-detail__live-banner__title {
      margin: 0 0 2px;
      color: #f8fafc;
      font-weight: 600;
    }
    .proj-detail__live-banner__reason {
      margin: 0 0 10px;
      color: rgba(248,250,252,0.85);
      font-style: italic;
    }
    .proj-detail__live-banner__reply textarea {
      width: 100%;
      box-sizing: border-box;
      background: rgba(0,0,0,0.30);
      color: #f8fafc;
      border: 1px solid rgba(255,255,255,0.18);
      border-radius: 6px;
      padding: 8px 10px;
      font: inherit;
      font-size: 0.84rem;
      resize: vertical;
      min-height: 64px;
    }
    .proj-detail__live-banner__reply textarea:focus {
      outline: none;
      border-color: rgba(248,113,113,0.65);
      box-shadow: 0 0 0 2px rgba(248,113,113,0.20);
    }
    .proj-detail__live-banner__actions {
      display: flex;
      align-items: center;
      gap: 10px;
      justify-content: flex-end;
      margin-top: 8px;
    }
    .proj-detail__live-banner__send {
      background: rgba(248,113,113,0.25);
      color: #fef2f2;
      border: 1px solid rgba(248,113,113,0.50);
      border-radius: 6px;
      padding: 6px 14px;
      font: inherit;
      font-size: 0.85rem;
      cursor: pointer;
    }
    .proj-detail__live-banner__send:hover:not(:disabled) {
      background: rgba(248,113,113,0.40);
    }
    .proj-detail__live-banner__send:disabled {
      opacity: 0.55;
      cursor: not-allowed;
    }
    .proj-detail__live-banner__error {
      color: #fda4af;
      font-size: 0.78rem;
    }

    .proj-detail__paths {
      display: grid;
      grid-template-columns: max-content 1fr;
      gap: 4px 12px;
      margin: 0 0 16px;
      font-size: 0.82rem;
    }
    .proj-detail__paths > div { display: contents; }
    .proj-detail__paths dt { color: rgba(255,255,255,0.55); }
    .proj-detail__paths dd { margin: 0; color: #cdd6f4; font-family: var(--font-mono, monospace); word-break: break-all; }

    /*
     * Section spacing: was 18px (tight, "super technisch" per user
     * feedback). Bump to 28px so each group reads as its own thought
     * with breathing room above and below. Section titles drop the
     * uppercase + 0.78rem treatment for a normal-case 0.95rem with
     * a softer divider line; closer to a doc, further from a control
     * panel.
     */
    .proj-detail__group { margin-bottom: 28px; }
    .proj-detail__group h3 {
      margin: 0 0 12px;
      padding-bottom: 6px;
      font-size: 0.95rem;
      font-weight: 600;
      color: #cbd5e1;
      letter-spacing: 0;
      text-transform: none;
      border-bottom: 1px solid rgba(255,255,255,0.06);
    }
    .proj-detail__hint {
      margin: 6px 0 0;
      color: rgba(255,255,255,0.55);
      font-size: 0.78rem;
    }
    .proj-detail__hint--inline { margin: 0 0 0 8px; }

    .proj-detail__modes { display: flex; flex-wrap: wrap; gap: 6px; }
    .proj-detail__mode {
      padding: 4px 10px;
      border-radius: 999px;
      border: 1px solid rgba(255,255,255,0.14);
      background: rgba(255,255,255,0.04);
      color: #a6adc8;
      font-size: 0.80rem;
      cursor: pointer;
    }
    .proj-detail__mode:hover { color: #cdd6f4; border-color: rgba(255,255,255,0.28); }
    .proj-detail__mode--active {
      background: #89b4fa;
      color: #1e1e2e;
      border-color: #89b4fa;
      font-weight: 600;
    }

    .proj-detail__model-row { display: flex; align-items: center; flex-wrap: wrap; gap: 6px; }
    .proj-detail__model-select {
      background: rgba(0,0,0,0.30);
      color: #cdd6f4;
      border: 1px solid rgba(255,255,255,0.14);
      border-radius: 6px;
      padding: 4px 8px;
      font-size: 0.85rem;
    }

    .proj-detail__toggle {
      display: inline-flex;
      align-items: center;
      gap: 8px;
      color: #cdd6f4;
      font-size: 0.85rem;
    }
    .proj-detail__toggle code {
      font-size: 0.78rem;
      background: rgba(255,255,255,0.06);
      padding: 1px 4px;
      border-radius: 3px;
    }

    .proj-detail__counts { display: flex; gap: 8px; flex-wrap: wrap; }
    .proj-detail__count {
      display: flex;
      flex-direction: column;
      align-items: center;
      min-width: 64px;
      padding: 6px 8px;
      border-radius: 6px;
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.10);
    }
    .proj-detail__count-num { color: #cdd6f4; font-size: 1.15rem; font-weight: 700; font-variant-numeric: tabular-nums; }
    .proj-detail__count-state { color: rgba(255,255,255,0.55); font-size: 0.70rem; text-transform: uppercase; letter-spacing: 0.04em; }

    /*
     * Recent-activity entries: drop the boxed "card" treatment and the
     * uppercase metadata bar. The summary itself is the headline; the
     * kind / topic / timestamp move below as quiet metadata. Looks
     * more like a feed of notes, less like a status table.
     */
    .proj-detail__entries { list-style: none; padding: 0; margin: 0; display: flex; flex-direction: column; gap: 14px; }
    .proj-detail__entry {
      padding: 0;
      border: none;
      border-left: 2px solid rgba(255,255,255,0.10);
      padding-left: 12px;
      background: none;
    }
    .proj-detail__entry--decision { border-left-color: rgba(196,181,253,0.50); }
    .proj-detail__entry--action { border-left-color: rgba(125,211,252,0.50); }
    .proj-detail__entry--observation { border-left-color: rgba(148,163,184,0.40); }
    .proj-detail__entry--intervention { border-left-color: rgba(249,226,175,0.55); }
    .proj-detail__entry p { margin: 0 0 6px; color: #e2e8f0; font-size: 0.92rem; line-height: 1.5; }
    .proj-detail__entry header {
      display: flex;
      gap: 10px;
      align-items: baseline;
      font-size: 0.76rem;
      letter-spacing: 0;
      color: rgba(255,255,255,0.50);
      text-transform: none;
    }
    .proj-detail__entry-kind { font-weight: 600; color: #cbd5e1; }
    .proj-detail__entry-topic { padding: 1px 6px; border-radius: 3px; background: rgba(255,255,255,0.05); font-family: var(--font-mono, monospace); font-size: 0.72rem; }
    .proj-detail__entry-ts { margin-left: auto; font-variant-numeric: tabular-nums; }

    .proj-detail__empty { color: rgba(255,255,255,0.5); font-style: italic; margin: 4px 0 0; font-size: 0.82rem; }

    .proj-detail__session dd code { font-size: 0.78rem; color: #c4b5fd; }
    .proj-detail__session-error { color: #fda4af; }
    .proj-detail__session-snap { margin: 6px 0; }
    .proj-detail__session-snap summary {
      cursor: pointer;
      color: rgba(255,255,255,0.55);
      font-size: 0.78rem;
      user-select: none;
    }
    .proj-detail__session-snap summary:hover { color: #cdd6f4; }
    .proj-detail__session-pre {
      max-height: 240px;
      overflow: auto;
      background: rgba(0,0,0,0.30);
      border: 1px solid rgba(255,255,255,0.08);
      border-radius: 6px;
      padding: 8px 10px;
      font-size: 0.78rem;
      color: #cdd6f4;
      white-space: pre-wrap;
      margin: 6px 0 0;
    }
  `]
})
export class ProjectDetailComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();
  readonly openFeed = output<string>();
  readonly openReport = output<AnalysisReport>();

  private readonly jobService = inject(JobService);

  readonly settings = signal<ProjectSettingsRow | null>(null);
  readonly runnerStatus = signal<RunnerStatus | null>(null);
  readonly grouped = signal<GroupedJobs | null>(null);
  readonly recentEntries = signal<OrchestratorLogEntry[]>([]);
  readonly orchSession = signal<OrchestratorSession | null>(null);
  readonly pendingDecisions = signal<ReadonlyArray<{ jobId: string; title: string; reason: string | null }>>([]);

  // ADR-0027: live, in-progress decision sentinels emitted by the running
  // job. Distinct from pendingDecisions (post-run, lane-scoped). Polled on
  // the same 5 s interval refreshAll uses; cleared by the backend the
  // moment the user replies (the [user] line resolves the sentinel).
  readonly livePendingDecisions = signal<ReadonlyArray<{ jobId: string; title: string; kind: string; reason: string | null; detectedAt: string }>>([]);
  readonly liveReplyDrafts: { [jobId: string]: string } = {};
  readonly liveReplySending: { [jobId: string]: boolean } = {};
  readonly liveReplyErrors: { [jobId: string]: string | null } = {};

  // Two-way bound drafts so the form is responsive even before the
  // server round-trip completes.
  autoCommitDraft = false;
  orchModelDraft = '';

  /**
   * Mode buttons. The four runner modes are: manual (off), auto-single
   * (run one then revert), auto-continuous (run all ready), paused
   * (deny auto-pickup but keep state). Click sends a PUT and refreshes.
   */
  readonly modes: ReadonlyArray<{ id: string; label: string; tooltip: string }> = [
    { id: 'manual', label: 'Manual', tooltip: 'Auto-pickup off; user starts each task.' },
    { id: 'auto-single', label: 'Auto · single', tooltip: 'Pick up the next ready task once, then revert to manual.' },
    { id: 'auto-continuous', label: 'Auto · continuous', tooltip: 'Pick up ready tasks continuously.' },
    { id: 'paused', label: 'Paused', tooltip: 'Hold all auto-pickup; manual starts still allowed.' }
  ];

  readonly orchModelOptions = OrchestratorRunner_KnownModels;

  readonly effectiveMode = computed(() => {
    const status = this.runnerStatus();
    if (!status) return this.settings()?.runnerMode ?? 'manual';
    const proj = status.projects?.[this.projectName()];
    return proj?.mode ?? this.settings()?.runnerMode ?? 'manual';
  });

  readonly modeHint = computed(() => {
    switch (this.effectiveMode()) {
      case 'auto-continuous':
        return 'Auto-pickup is running. The runner will pick up the next ready task as soon as the current one finishes.';
      case 'auto-single':
        return 'After the next pickup, the runner reverts to Manual.';
      case 'paused':
        return 'Auto-pickup is held. Manual starts still work.';
      default:
        return 'No auto-pickup. You start each task manually.';
    }
  });

  readonly paths = computed(() => {
    // Watch-paths come back from /api/watch-paths via JobService and are
    // already cached in JobService when the app boots; we look them up
    // through the runnerStatus's project record where available.
    // (Path strings are not exposed there; fall back to "(unknown)".)
    const status = this.runnerStatus();
    const proj = status?.projects?.[this.projectName()];
    return {
      path: this.projectName(),
      rootPath: proj?.activeJobId ? '' : '',
      repositoryPath: ''
    };
  });

  readonly laneCounts = computed(() => {
    const grouped = this.grouped();
    if (!grouped) return [] as ReadonlyArray<{ state: string; label: string; count: number }>;
    const proj = this.projectName();
    const c = (jobs: ReadonlyArray<{ projectName: string }>) => jobs.filter(j => j.projectName === proj).length;
    return [
      { state: '0-backlog',     label: 'Backlog',     count: c(grouped.backlog ?? []) },
      { state: '1-preparation', label: 'Preparation', count: c(grouped.preparation) },
      { state: '2-ready',       label: 'Ready',       count: c(grouped.ready) },
      { state: '3-progress',    label: 'Progress',    count: c(grouped.progress) },
      { state: '4-auto-review', label: 'Auto Review', count: c(grouped.autoReview ?? grouped.review) },
      { state: '5-human-review',label: 'Human Review',count: c(grouped.humanReview ?? []) },
      { state: '6-completed',   label: 'Completed',   count: c(grouped.completed) },
      { state: '7-archive',     label: 'Archive',     count: c(grouped.archive) }
    ];
  });

  readonly tokenTotalLabel = computed(() => {
    const entries = this.recentEntries();
    let input = 0, output = 0, count = 0;
    for (const e of entries) {
      if (!e.tokenUsage) continue;
      input += e.tokenUsage.inputTokens;
      output += e.tokenUsage.outputTokens;
      count++;
    }
    if (count === 0) return `${entries.length} entries; no orchestrator LLM calls yet.`;
    return `${entries.length} entries; ${count} orchestrator LLM call${count === 1 ? '' : 's'}: ↑${input.toLocaleString()} / ↓${output.toLocaleString()} tokens.`;
  });

  private pollTimer: ReturnType<typeof setInterval> | null = null;

  ngOnInit(): void {
    this.refreshAll();
    this.pollTimer = setInterval(() => this.refreshAll(true), 5_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  refreshAll(silent = false): void {
    void silent;
    this.jobService.getAllProjectSettings().subscribe({
      next: (all) => {
        const row = all[this.projectName()] ?? { autoCommit: false, runnerMode: null, orchestratorModel: null };
        this.settings.set(row);
        // Sync drafts only when the server-known value differs (so a
        // user mid-edit isn't yanked back to a stale value).
        if (this.autoCommitDraft !== row.autoCommit) this.autoCommitDraft = row.autoCommit;
        const wantedModel = row.orchestratorModel ?? '';
        if (this.orchModelDraft !== wantedModel) this.orchModelDraft = wantedModel;
      },
      error: () => { /* silent; keep last value */ }
    });
    this.jobService.getRunnerStatus().subscribe({
      next: (s) => this.runnerStatus.set(s),
      error: () => {}
    });
    this.jobService.refresh(true);
    // Read latest grouped from the service signal one tick later.
    setTimeout(() => this.grouped.set(this.jobService.grouped()), 50);
    this.jobService.getOrchestratorLog(this.projectName()).subscribe({
      next: (resp) => {
        // Show the last 5 entries newest-first.
        const all = resp.entries ?? [];
        this.recentEntries.set(all.slice(-5).reverse());
      },
      error: () => {}
    });
    this.jobService.getOrchestratorSession(this.projectName()).subscribe({
      next: (resp) => this.orchSession.set(resp.session),
      error: () => {}
    });
    this.jobService.getReviewDecisionsPending(this.projectName()).subscribe({
      next: (resp) => this.pendingDecisions.set(resp.items ?? []),
      error: () => this.pendingDecisions.set([])
    });
    this.jobService.getRunnerPendingDecisions(this.projectName()).subscribe({
      next: (resp) => this.livePendingDecisions.set(resp.items ?? []),
      error: () => this.livePendingDecisions.set([])
    });
  }

  /**
   * Send a reply to a live decision sentinel through the existing
   * /api/jobs/{jobId}/continue endpoint with mode 'steer'. The sentinel
   * resolves on the backend's next tick (the [user] log line cancels it),
   * which clears the banner without an explicit dismiss.
   */
  sendLiveDecisionReply(jobId: string): void {
    const text = (this.liveReplyDrafts[jobId] ?? '').trim();
    if (!text) return;
    this.liveReplySending[jobId] = true;
    this.liveReplyErrors[jobId] = null;
    this.jobService.continueJob(jobId, text, undefined, undefined, undefined, 'steer').subscribe({
      next: () => {
        this.liveReplyDrafts[jobId] = '';
        this.liveReplySending[jobId] = false;
        // Optimistically clear the banner; the next refresh tick will
        // re-confirm from the backend.
        this.livePendingDecisions.set(
          this.livePendingDecisions().filter(p => p.jobId !== jobId)
        );
        this.refreshAll(true);
      },
      error: (err) => {
        this.liveReplySending[jobId] = false;
        this.liveReplyErrors[jobId] = err?.error?.error || err?.message || 'Failed to send reply.';
      }
    });
  }

  setMode(mode: string): void {
    this.jobService.setRunnerMode(this.projectName(), mode).subscribe({
      next: () => this.refreshAll(true),
      error: () => {}
    });
  }

  onAutoCommitChange(): void {
    this.jobService.setProjectAutoCommit(this.projectName(), this.autoCommitDraft).subscribe({
      next: () => this.refreshAll(true),
      error: () => {}
    });
  }

  onOrchModelChange(): void {
    const model = this.orchModelDraft.trim();
    this.jobService.setProjectOrchestratorModel(this.projectName(), model || null).subscribe({
      next: () => this.refreshAll(true),
      error: () => {}
    });
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
