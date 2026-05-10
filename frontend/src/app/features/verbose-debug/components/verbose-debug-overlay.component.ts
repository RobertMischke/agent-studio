import {
  ChangeDetectionStrategy,
  Component,
  computed,
  input,
  output,
  signal
} from '@angular/core';
import { CommonModule } from '@angular/common';
import type { CliOutputLine, JobInfo } from '../../../models/job.model';
import type { RunRecord, RunTimeline } from '../../../features/run-timeline';
import type { JobScreenshot } from '../../../features/screenshots';
import type { JobTokenSummary } from '../../../features/tokens';
import { projectConversation } from '../../../components/chat/conversation-projection';
import type {
  ConversationEvent,
  RawLineRange,
  ToolFamily,
  WorkbenchDebugEvent
} from '../../../components/chat/conversation-event';
import { formatTokens as fmtTokens } from '../../../services/format.util';

export type VerboseDebugTab =
  | 'overview'
  | 'actors'
  | 'orchestrator'
  | 'tools'
  | 'warnings'
  | 'tasks'
  | 'tokens'
  | 'artifacts'
  | 'trace';

interface ActorRow {
  key: string;
  label: string;
  count: number;
  glyph: string;
}

interface ToolRow {
  family: ToolFamily;
  label: string;
  count: number;
  failures: number;
  percent: number;
}

interface WarningRow {
  key: string;
  label: string;
  count: number;
  tone: 'info' | 'warn' | 'danger';
  description: string;
}

interface TokenRow {
  key: string;
  scope: string;
  inputTokens: number;
  outputTokens: number;
  total: number;
  percent: number;
}

interface ArtifactRow {
  caption: string;
  durablePath: string;
  sourcePath: string;
  url?: string;
  status?: string | null;
  timestamp?: string;
}

interface TraceLinkRow {
  label: string;
  start: number;
  end: number;
  range: RawLineRange;
  kind: string;
}

const TOOL_LABELS: Record<ToolFamily, string> = {
  read: 'Read',
  search: 'Search',
  command: 'Command',
  edit: 'Edit',
  task: 'Task',
  todo: 'Todo',
  other: 'Other'
};

/**
 * Read-only fullscreen "Verbose Debug" overlay. Surfaces actor activity,
 * orchestrator decisions, supervisor advisories, run timing, tool density,
 * warning density, task markers, token usage, artifacts, raw trace links,
 * and a human explanation derived from the same `ConversationEvent`
 * projection the chat lens uses. The composer is intentionally absent:
 * this is the diagnostic escape hatch, not the default chat surface.
 */
@Component({
  selector: 'app-verbose-debug-overlay',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [CommonModule],
  template: `
    <div class="vdbg" data-testid="verbose-debug-overlay" [attr.data-theme]="theme()" (click)="close.emit()">
      <section class="vdbg__panel" role="dialog" aria-label="Verbose debug" (click)="$event.stopPropagation()">
        <header class="vdbg__header">
          <div class="vdbg__title-block">
            <strong class="vdbg__title">🐞 Verbose Debug</strong>
            <span class="vdbg__subtitle">{{ subtitle() }}</span>
          </div>
          <div class="vdbg__head-actions">
            <button class="vdbg__theme"
                    type="button"
                    data-testid="verbose-debug-theme-toggle"
                    [title]="theme() === 'light' ? 'Switch to dark' : 'Switch to light'"
                    (click)="toggleTheme()">{{ theme() === 'light' ? '🌙' : '☀' }}</button>
            <button class="vdbg__close"
                    type="button"
                    data-testid="verbose-debug-close"
                    (click)="close.emit()">✕ Close</button>
          </div>
        </header>

        <nav class="vdbg__tabs" role="tablist" aria-label="Verbose debug sections" data-testid="verbose-debug-tabs">
          @for (tab of tabs; track tab.id) {
            <button class="vdbg__tab"
                    type="button"
                    role="tab"
                    [attr.aria-selected]="activeTab() === tab.id"
                    [class.vdbg__tab--active]="activeTab() === tab.id"
                    [attr.data-testid]="'verbose-debug-tab-' + tab.id"
                    (click)="activeTab.set(tab.id)">
              <span aria-hidden="true">{{ tab.icon }}</span>
              <span>{{ tab.label }}</span>
              @if (tabBadge(tab.id); as badge) {
                <span class="vdbg__tab-badge" aria-hidden="true">{{ badge }}</span>
              }
            </button>
          }
        </nav>

        <div class="vdbg__body" data-testid="verbose-debug-body">
          @switch (activeTab()) {
            @case ('overview') {
              <article class="vdbg__card vdbg__card--explanation">
                <h3>What this view shows</h3>
                <p>
                  The default chat hides noise so reading stays compact. This page is the deliberate escape hatch
                  for when a run looks confusing: it explains how often each actor was active, how long the run
                  took, where tokens went, and which evidence backs the current state. It is read-only — every row
                  links back to the raw activity log so the chat lens never replaces the source of truth.
                </p>
              </article>

              <article class="vdbg__card">
                <h3>Run timing</h3>
                <div class="vdbg__metrics">
                  <div><b data-testid="verbose-debug-metric-runs">{{ runStats().runCount }}</b><span>runs</span></div>
                  <div><b>{{ runStats().completedCount }}</b><span>completed</span></div>
                  <div><b>{{ runStats().failedCount }}</b><span>failed</span></div>
                  <div><b>{{ runStats().cancelledCount }}</b><span>cancelled</span></div>
                  <div><b>{{ formatDuration(totalDurationSeconds()) }}</b><span>total wall time</span></div>
                  <div><b>{{ activeRunBadge() }}</b><span>active</span></div>
                </div>
              </article>

              <article class="vdbg__card">
                <h3>At a glance</h3>
                <div class="vdbg__metrics">
                  <div><b data-testid="verbose-debug-metric-tools">{{ toolDensity().total }}</b><span>tool calls</span></div>
                  <div><b>{{ toolDensity().failures }}</b><span>tool failures</span></div>
                  <div><b>{{ totalWarnings() }}</b><span>warnings</span></div>
                  <div><b>{{ formatTokens(totalTokens()) }}</b><span>tokens</span></div>
                  <div><b>{{ artifactRows().length }}</b><span>artifacts</span></div>
                  <div><b>{{ taskScopeLabel() }}</b><span>task lane</span></div>
                </div>
              </article>

              <article class="vdbg__card">
                <h3>Density timeline</h3>
                @for (row of overviewBands(); track row.name) {
                  <div class="vdbg__band">
                    <span class="vdbg__band-label">{{ row.name }}</span>
                    <i class="vdbg__band-bar"><em [style.width.%]="row.percent"></em></i>
                    <b class="vdbg__band-value">{{ row.value }}</b>
                  </div>
                }
                @if (overviewBands().length === 0) {
                  <p class="vdbg__empty">No timing or density signal yet. Start a run to populate this view.</p>
                }
              </article>
            }

            @case ('actors') {
              <article class="vdbg__card">
                <h3>Actor activity counts</h3>
                <p class="vdbg__hint">
                  How often each conversational actor showed up between user inputs and the latest event. Filter the
                  raw activity log by actor with the row buttons.
                </p>
                <div class="vdbg__rows" data-testid="verbose-debug-actor-rows">
                  @for (row of actorRows(); track row.key) {
                    <div class="vdbg__row" [attr.data-testid]="'verbose-debug-actor-row-' + row.key">
                      <span class="vdbg__row-icon" aria-hidden="true">{{ row.glyph }}</span>
                      <span class="vdbg__row-label">{{ row.label }}</span>
                      <i class="vdbg__row-bar"><em [style.width.%]="rowPercent(row.count, maxActorCount())"></em></i>
                      <b class="vdbg__row-value">{{ row.count }}</b>
                    </div>
                  }
                  @if (actorRows().length === 0) {
                    <p class="vdbg__empty">No actor activity yet.</p>
                  }
                </div>
              </article>
              <article class="vdbg__card">
                <h3>Supervisor advisories</h3>
                <p class="vdbg__hint">Watchdog quiet/resume/kill signals reach this run from Layer 2. They never replace the raw trace.</p>
                <div class="vdbg__metrics" data-testid="verbose-debug-supervisor-counts">
                  <div><b>{{ warningCounts().watchdogQuiet }}</b><span>quiet</span></div>
                  <div><b>{{ warningCounts().watchdogKills }}</b><span>kills</span></div>
                  <div><b>{{ supervisorWaits().filter(supervisorIsResumed).length }}</b><span>resumed</span></div>
                </div>
              </article>
            }

            @case ('orchestrator') {
              <article class="vdbg__card">
                <h3>Orchestrator actions</h3>
                <p class="vdbg__hint">
                  Decision and reissue rows from the orchestrator stream and decision events, ordered by occurrence.
                </p>
                <div class="vdbg__decision-list" data-testid="verbose-debug-orchestrator-list">
                  @for (d of orchestratorDecisions(); track d.id) {
                    <div class="vdbg__decision">
                      <header>
                        <span class="vdbg__decision-kind"
                              [attr.data-tone]="d.severity ?? 'info'">{{ d.decisionType }}</span>
                        <span class="vdbg__decision-action">→ {{ d.action || '—' }}</span>
                        <span class="vdbg__decision-time">{{ formatTime(d.timestamp) }}</span>
                      </header>
                      <p>{{ d.reason }}</p>
                      @if (d.evidence) {
                        <p class="vdbg__decision-evidence">{{ d.evidence }}</p>
                      }
                      <footer>
                        @if (d.retryBudget) {
                          <span>retry {{ d.retryBudget.used }}/{{ d.retryBudget.max }}</span>
                        }
                        @if (d.tokenUsage) {
                          <span>tokens ↑ {{ formatTokens(d.tokenUsage.inputTokens) }} / ↓ {{ formatTokens(d.tokenUsage.outputTokens) }}</span>
                        }
                        <button type="button"
                                class="vdbg__link-btn"
                                data-testid="verbose-debug-orchestrator-trace"
                                (click)="emitTrace(d.rawRange)">Open raw trace lines {{ d.rawRange.start }}–{{ d.rawRange.end }}</button>
                      </footer>
                    </div>
                  }
                  @if (orchestratorDecisions().length === 0) {
                    <p class="vdbg__empty">No orchestrator decisions in this run.</p>
                  }
                </div>
              </article>
            }

            @case ('tools') {
              <article class="vdbg__card">
                <h3>Tool density</h3>
                <p class="vdbg__hint">
                  Per-family aggregate from collapsed tool bursts. The chat lens shows one chip; this is the breakdown.
                </p>
                <div class="vdbg__rows" data-testid="verbose-debug-tool-rows">
                  @for (row of toolRows(); track row.family) {
                    <div class="vdbg__row" [attr.data-testid]="'verbose-debug-tool-row-' + row.family">
                      <span class="vdbg__row-label">{{ row.label }}</span>
                      <i class="vdbg__row-bar"><em [style.width.%]="row.percent"></em></i>
                      <b class="vdbg__row-value">{{ row.count }}</b>
                      @if (row.failures > 0) {
                        <span class="vdbg__row-failures" title="Failed tool invocations in this family">{{ row.failures }} ✗</span>
                      }
                    </div>
                  }
                  @if (toolRows().length === 0) {
                    <p class="vdbg__empty">No tool activity recorded.</p>
                  }
                </div>
                @if (toolBursts().length > 0) {
                  <p class="vdbg__hint">
                    {{ toolBursts().length }} burst{{ toolBursts().length === 1 ? '' : 's' }} ·
                    {{ toolDensity().total }} call{{ toolDensity().total === 1 ? '' : 's' }} ·
                    {{ toolDensity().failures }} failure{{ toolDensity().failures === 1 ? '' : 's' }}
                  </p>
                }
              </article>
            }

            @case ('warnings') {
              <article class="vdbg__card">
                <h3>Warning density</h3>
                <p class="vdbg__hint">
                  Parser warnings, capture failures, schema drift, watchdog events, and needs-input loops. Hidden from the
                  compact chat by default; visible here for triage.
                </p>
                <div class="vdbg__rows" data-testid="verbose-debug-warning-rows">
                  @for (row of warningRows(); track row.key) {
                    <div class="vdbg__row" [attr.data-testid]="'verbose-debug-warning-row-' + row.key" [attr.data-tone]="row.tone">
                      <span class="vdbg__row-label">{{ row.label }}</span>
                      <span class="vdbg__row-meta">{{ row.description }}</span>
                      <b class="vdbg__row-value">{{ row.count }}</b>
                    </div>
                  }
                </div>
              </article>
            }

            @case ('tasks') {
              <article class="vdbg__card">
                <h3>Task and run markers</h3>
                <p class="vdbg__hint">Per-run start/finish events with duration, exit code, and trace range.</p>
                <div class="vdbg__rows" data-testid="verbose-debug-task-rows">
                  @if (job(); as info) {
                    <div class="vdbg__row" data-testid="verbose-debug-task-summary">
                      <span class="vdbg__row-icon" aria-hidden="true">🎯</span>
                      <span class="vdbg__row-label">{{ info.title }}</span>
                      <span class="vdbg__row-meta">lane: {{ info.state }}</span>
                    </div>
                  }
                  @for (run of runs(); track run.index) {
                    <div class="vdbg__row" [attr.data-testid]="'verbose-debug-run-row-' + run.index">
                      <span class="vdbg__row-icon" aria-hidden="true">▶</span>
                      <span class="vdbg__row-label">Run #{{ run.index }} · {{ run.intent }}</span>
                      <span class="vdbg__row-meta">
                        {{ run.cli || '?' }} · {{ run.status }} · {{ formatDuration(run.durationSeconds ?? 0) }}
                        @if (run.exitCode != null) {
                          · exit {{ run.exitCode }}
                        }
                      </span>
                      @if (run.lineStart && run.lineEnd) {
                        <button type="button"
                                class="vdbg__link-btn"
                                [attr.data-testid]="'verbose-debug-run-trace-' + run.index"
                                (click)="emitTrace({ source: traceSource(), start: run.lineStart!, end: run.lineEnd! })">Trace {{ run.lineStart }}–{{ run.lineEnd }}</button>
                      }
                    </div>
                  }
                  @if (runs().length === 0) {
                    <p class="vdbg__empty">No runs recorded yet.</p>
                  }
                </div>
              </article>
            }

            @case ('tokens') {
              <article class="vdbg__card">
                <h3>Token usage</h3>
                <p class="vdbg__hint">
                  Token totals collected from the conversation projection. The chat lens shows pressure and trend; this view
                  splits attribution across task agent, orchestrator, and any supporting jobs the projection produced events for.
                </p>
                <div class="vdbg__rows" data-testid="verbose-debug-token-rows">
                  @for (row of tokenRows(); track row.key) {
                    <div class="vdbg__row" [attr.data-testid]="'verbose-debug-token-row-' + row.key">
                      <span class="vdbg__row-label">{{ row.scope }}</span>
                      <i class="vdbg__row-bar"><em [style.width.%]="row.percent"></em></i>
                      <b class="vdbg__row-value">{{ formatTokens(row.total) }}</b>
                      <span class="vdbg__row-meta">↑ {{ formatTokens(row.inputTokens) }} · ↓ {{ formatTokens(row.outputTokens) }}</span>
                    </div>
                  }
                  @if (tokenRows().length === 0) {
                    <p class="vdbg__empty">No token attribution available yet.</p>
                  }
                </div>
                @if (jobTokenSummary(); as t) {
                  <p class="vdbg__hint" data-testid="verbose-debug-token-orchestrator">
                    Orchestrator post-run summarizer/grader rolled up {{ t.calls }} call{{ t.calls === 1 ? '' : 's' }} on
                    {{ t.lastModel || '?' }} (cache read {{ formatTokens(t.cacheReadTokens) }}, cache write {{ formatTokens(t.cacheCreationTokens) }}).
                  </p>
                }
              </article>
            }

            @case ('artifacts') {
              <article class="vdbg__card">
                <h3>Artifacts and screenshots</h3>
                <p class="vdbg__hint">Durable evidence under <code>results/</code>. Scratch paths are kept so you can trace the original.</p>
                <div class="vdbg__artifacts" data-testid="verbose-debug-artifact-rows">
                  @for (row of artifactRows(); track row.durablePath || row.sourcePath) {
                    <figure class="vdbg__artifact" [attr.data-testid]="'verbose-debug-artifact-row'">
                      @if (row.url) {
                        <img [attr.src]="row.url" [attr.alt]="row.caption" loading="lazy" />
                      } @else {
                        <div class="vdbg__artifact-placeholder" aria-hidden="true">📷</div>
                      }
                      <figcaption>
                        <strong>{{ row.caption || 'screenshot' }}</strong>
                        <code>{{ row.durablePath || row.sourcePath }}</code>
                        @if (row.status) {
                          <span class="vdbg__artifact-status" [attr.data-status]="row.status">{{ row.status }}</span>
                        }
                      </figcaption>
                    </figure>
                  }
                  @if (artifactRows().length === 0) {
                    <p class="vdbg__empty">No screenshots or artifacts attached to this run.</p>
                  }
                </div>
              </article>
            }

            @case ('trace') {
              <article class="vdbg__card">
                <h3>Raw trace ranges</h3>
                <p class="vdbg__hint">
                  Every chat-lens event keeps a back-reference to the raw activity log. Click a row to copy the line range
                  or hand it to the host's trace viewer.
                </p>
                <div class="vdbg__rows" data-testid="verbose-debug-trace-rows">
                  @for (row of traceLinkRows(); track row.label) {
                    <div class="vdbg__row" [attr.data-testid]="'verbose-debug-trace-row'">
                      <span class="vdbg__row-icon" aria-hidden="true">📜</span>
                      <span class="vdbg__row-label">{{ row.kind }}</span>
                      <span class="vdbg__row-meta">{{ row.label }}</span>
                      <button type="button"
                              class="vdbg__link-btn"
                              data-testid="verbose-debug-trace-open"
                              (click)="emitTrace(row.range)">Open lines {{ row.start }}–{{ row.end }}</button>
                    </div>
                  }
                  @if (traceLinkRows().length === 0) {
                    <p class="vdbg__empty">No trace ranges yet.</p>
                  }
                </div>
              </article>
              <article class="vdbg__card">
                <h3>Source pointer</h3>
                <p class="vdbg__hint">
                  The raw Activity Log remains one click away. Trace ranges are 1-based line numbers into <code>cli-output.log</code>.
                </p>
                @if (latestResult()) {
                  <p data-testid="verbose-debug-latest-result"><strong>Latest result:</strong> {{ latestResult() }}</p>
                }
              </article>
            }
          }
        </div>
      </section>
    </div>
  `,
  styles: [`
    :host { position: fixed; inset: 0; z-index: 6000; display: block; }
    .vdbg {
      position: fixed;
      inset: 0;
      background: rgba(7, 9, 14, 0.72);
      display: flex;
      align-items: stretch;
      justify-content: center;
      padding: 24px;
      overflow: auto;
      --vdbg-bg: #0f1320;
      --vdbg-surface: #161a2a;
      --vdbg-border: rgba(255,255,255,0.08);
      --vdbg-text: #e2e8f0;
      --vdbg-muted: #94a3b8;
      --vdbg-accent: #c4b5fd;
      --vdbg-warn: #f59e0b;
      --vdbg-danger: #f87171;
      --vdbg-ok: #4ade80;
      --vdbg-band: rgba(196, 181, 253, 0.18);
      --vdbg-band-fill: linear-gradient(90deg, rgba(99,102,241,0.55), rgba(196,181,253,0.85));
    }
    .vdbg[data-theme='light'] {
      background: rgba(15, 23, 42, 0.32);
      --vdbg-bg: #f8fafc;
      --vdbg-surface: #ffffff;
      --vdbg-border: rgba(15, 23, 42, 0.08);
      --vdbg-text: #1e293b;
      --vdbg-muted: #475569;
      --vdbg-accent: #6366f1;
      --vdbg-warn: #b45309;
      --vdbg-danger: #b91c1c;
      --vdbg-ok: #047857;
      --vdbg-band: rgba(99, 102, 241, 0.10);
      --vdbg-band-fill: linear-gradient(90deg, rgba(99,102,241,0.35), rgba(196,181,253,0.85));
    }
    .vdbg__panel {
      width: min(1280px, 100%);
      max-height: 100%;
      display: flex;
      flex-direction: column;
      background: var(--vdbg-bg);
      color: var(--vdbg-text);
      border: 1px solid var(--vdbg-border);
      border-radius: 16px;
      box-shadow: 0 24px 60px rgba(0,0,0,0.45);
      overflow: hidden;
    }
    .vdbg__header {
      display: flex;
      justify-content: space-between;
      align-items: center;
      gap: 16px;
      padding: 14px 18px;
      border-bottom: 1px solid var(--vdbg-border);
      background: linear-gradient(180deg, rgba(196,181,253,0.10), transparent 70%);
    }
    .vdbg__title-block { display: flex; flex-direction: column; gap: 2px; min-width: 0; }
    .vdbg__title { font-size: 15px; letter-spacing: 0.02em; }
    .vdbg__subtitle { color: var(--vdbg-muted); font-size: 12px; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .vdbg__head-actions { display: flex; gap: 6px; align-items: center; }
    .vdbg__theme, .vdbg__close {
      background: transparent;
      color: var(--vdbg-text);
      border: 1px solid var(--vdbg-border);
      border-radius: 8px;
      padding: 6px 10px;
      font-size: 12px;
      cursor: pointer;
    }
    .vdbg__close { font-weight: 600; }
    .vdbg__close:hover, .vdbg__theme:hover { background: rgba(196,181,253,0.16); }

    .vdbg__tabs {
      display: flex;
      gap: 4px;
      padding: 8px 12px;
      border-bottom: 1px solid var(--vdbg-border);
      overflow-x: auto;
      scrollbar-width: thin;
      background: var(--vdbg-surface);
    }
    .vdbg__tab {
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 6px 10px;
      background: transparent;
      color: var(--vdbg-muted);
      border: 1px solid transparent;
      border-radius: 8px;
      font-size: 12px;
      cursor: pointer;
      white-space: nowrap;
    }
    .vdbg__tab:hover { background: rgba(196,181,253,0.10); color: var(--vdbg-text); }
    .vdbg__tab--active {
      background: rgba(196,181,253,0.18);
      color: var(--vdbg-text);
      border-color: rgba(196,181,253,0.45);
      font-weight: 600;
    }
    .vdbg__tab-badge {
      background: rgba(196,181,253,0.30);
      color: var(--vdbg-text);
      border-radius: 999px;
      padding: 0 6px;
      min-width: 18px;
      height: 18px;
      display: inline-flex;
      align-items: center;
      justify-content: center;
      font-size: 10.5px;
      font-weight: 600;
    }

    .vdbg__body {
      flex: 1 1 auto;
      min-height: 0;
      overflow: auto;
      padding: 16px;
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
      gap: 14px;
      align-content: start;
    }
    .vdbg__card {
      background: var(--vdbg-surface);
      border: 1px solid var(--vdbg-border);
      border-radius: 12px;
      padding: 14px;
      display: flex;
      flex-direction: column;
      gap: 10px;
      min-width: 0;
    }
    .vdbg__card--explanation { grid-column: 1 / -1; }
    .vdbg__card h3 { margin: 0; font-size: 13px; letter-spacing: 0.04em; color: var(--vdbg-text); }
    .vdbg__card p { margin: 0; color: var(--vdbg-muted); font-size: 12.5px; line-height: 1.5; }
    .vdbg__card code { font-size: 11.5px; color: var(--vdbg-text); }
    .vdbg__hint { color: var(--vdbg-muted); font-size: 12px; }
    .vdbg__empty { color: var(--vdbg-muted); font-size: 12px; font-style: italic; }
    .vdbg__metrics {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(110px, 1fr));
      gap: 8px;
    }
    .vdbg__metrics div {
      background: rgba(196,181,253,0.06);
      border: 1px solid var(--vdbg-border);
      border-radius: 8px;
      padding: 8px 10px;
      display: flex;
      flex-direction: column;
      gap: 2px;
    }
    .vdbg__metrics b { font-size: 16px; font-weight: 600; }
    .vdbg__metrics span { font-size: 10.5px; color: var(--vdbg-muted); text-transform: uppercase; letter-spacing: 0.06em; }

    .vdbg__rows { display: flex; flex-direction: column; gap: 6px; }
    .vdbg__row {
      display: grid;
      grid-template-columns: auto minmax(0, 1fr) minmax(80px, 2fr) auto auto;
      gap: 10px;
      align-items: center;
      padding: 8px 10px;
      background: rgba(196,181,253,0.04);
      border: 1px solid var(--vdbg-border);
      border-radius: 8px;
      font-size: 12px;
    }
    .vdbg__row[data-tone='warn'] { border-color: rgba(245, 158, 11, 0.45); }
    .vdbg__row[data-tone='danger'] { border-color: rgba(248, 113, 113, 0.45); }
    .vdbg__row-icon { font-size: 14px; }
    .vdbg__row-label { color: var(--vdbg-text); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .vdbg__row-meta { color: var(--vdbg-muted); font-size: 11.5px; }
    .vdbg__row-value { color: var(--vdbg-text); font-family: var(--font-mono, ui-monospace, monospace); }
    .vdbg__row-failures { color: var(--vdbg-danger); font-weight: 600; }
    .vdbg__row-bar {
      display: block;
      height: 6px;
      background: var(--vdbg-band);
      border-radius: 999px;
      overflow: hidden;
    }
    .vdbg__row-bar em {
      display: block;
      height: 100%;
      background: var(--vdbg-band-fill);
      border-radius: inherit;
      min-width: 4px;
      transition: width 0.18s ease;
    }

    .vdbg__band {
      display: grid;
      grid-template-columns: minmax(120px, 1fr) 2fr auto;
      gap: 10px;
      align-items: center;
      font-size: 12px;
    }
    .vdbg__band-bar {
      display: block;
      height: 6px;
      background: var(--vdbg-band);
      border-radius: 999px;
      overflow: hidden;
    }
    .vdbg__band-bar em {
      display: block;
      height: 100%;
      background: var(--vdbg-band-fill);
      border-radius: inherit;
      min-width: 4px;
    }
    .vdbg__band-value { font-family: var(--font-mono, ui-monospace, monospace); }

    .vdbg__decision-list { display: flex; flex-direction: column; gap: 8px; }
    .vdbg__decision {
      border: 1px solid var(--vdbg-border);
      border-radius: 8px;
      padding: 10px;
      background: rgba(99, 102, 241, 0.06);
      display: flex;
      flex-direction: column;
      gap: 6px;
    }
    .vdbg__decision header { display: flex; gap: 8px; flex-wrap: wrap; align-items: baseline; }
    .vdbg__decision-kind {
      text-transform: uppercase;
      letter-spacing: 0.06em;
      font-size: 10.5px;
      padding: 2px 6px;
      border-radius: 6px;
      background: rgba(196,181,253,0.18);
      color: var(--vdbg-text);
    }
    .vdbg__decision-kind[data-tone='warn'] { background: rgba(245, 158, 11, 0.22); color: var(--vdbg-warn); }
    .vdbg__decision-kind[data-tone='error'] { background: rgba(248, 113, 113, 0.22); color: var(--vdbg-danger); }
    .vdbg__decision-action { color: var(--vdbg-muted); font-size: 12px; }
    .vdbg__decision-time { margin-left: auto; color: var(--vdbg-muted); font-size: 11px; }
    .vdbg__decision-evidence { font-style: italic; color: var(--vdbg-muted); }
    .vdbg__decision footer { display: flex; gap: 12px; flex-wrap: wrap; font-size: 11.5px; color: var(--vdbg-muted); }

    .vdbg__link-btn {
      background: transparent;
      color: var(--vdbg-accent);
      border: 1px solid rgba(196, 181, 253, 0.35);
      border-radius: 6px;
      padding: 3px 8px;
      font-size: 11.5px;
      cursor: pointer;
    }
    .vdbg__link-btn:hover { background: rgba(196,181,253,0.12); }

    .vdbg__artifacts {
      display: grid;
      grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
      gap: 10px;
    }
    .vdbg__artifact {
      margin: 0;
      display: flex;
      flex-direction: column;
      gap: 6px;
      border: 1px solid var(--vdbg-border);
      border-radius: 8px;
      overflow: hidden;
      background: rgba(196,181,253,0.04);
    }
    .vdbg__artifact img { width: 100%; height: 120px; object-fit: cover; display: block; }
    .vdbg__artifact-placeholder {
      width: 100%;
      height: 120px;
      display: flex;
      align-items: center;
      justify-content: center;
      font-size: 32px;
      color: var(--vdbg-muted);
      background: rgba(196,181,253,0.06);
    }
    .vdbg__artifact figcaption {
      padding: 8px 10px;
      display: flex;
      flex-direction: column;
      gap: 2px;
      font-size: 11.5px;
    }
    .vdbg__artifact figcaption strong { font-size: 12px; color: var(--vdbg-text); }
    .vdbg__artifact figcaption code { color: var(--vdbg-muted); white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
    .vdbg__artifact-status {
      display: inline-block;
      padding: 1px 6px;
      border-radius: 999px;
      font-size: 10.5px;
      letter-spacing: 0.06em;
      text-transform: uppercase;
      background: rgba(196,181,253,0.18);
      color: var(--vdbg-text);
      width: fit-content;
    }
    .vdbg__artifact-status[data-status='passed'] { background: rgba(74, 222, 128, 0.20); color: var(--vdbg-ok); }
    .vdbg__artifact-status[data-status='failed'] { background: rgba(248, 113, 113, 0.22); color: var(--vdbg-danger); }

    @media (prefers-color-scheme: light) {
      .vdbg:not([data-theme='dark']) {
        background: rgba(15, 23, 42, 0.32);
        --vdbg-bg: #f8fafc;
        --vdbg-surface: #ffffff;
        --vdbg-border: rgba(15, 23, 42, 0.08);
        --vdbg-text: #1e293b;
        --vdbg-muted: #475569;
        --vdbg-accent: #6366f1;
        --vdbg-warn: #b45309;
        --vdbg-danger: #b91c1c;
        --vdbg-ok: #047857;
        --vdbg-band: rgba(99, 102, 241, 0.10);
        --vdbg-band-fill: linear-gradient(90deg, rgba(99,102,241,0.35), rgba(196,181,253,0.85));
      }
    }

    @media (max-width: 768px) {
      .vdbg { padding: 8px; }
      .vdbg__panel { border-radius: 10px; }
      .vdbg__body { grid-template-columns: 1fr; padding: 10px; gap: 10px; }
      .vdbg__row {
        grid-template-columns: minmax(0, 1fr) auto;
        grid-auto-rows: auto;
      }
      .vdbg__row .vdbg__row-bar,
      .vdbg__row .vdbg__row-meta { grid-column: 1 / -1; }
      .vdbg__metrics { grid-template-columns: 1fr 1fr; }
      .vdbg__tabs { padding: 6px 8px; }
      .vdbg__tab { padding: 5px 8px; }
    }
  `]
})
export class VerboseDebugOverlayComponent {
  readonly lines = input<ReadonlyArray<CliOutputLine>>([]);
  readonly runTimeline = input<RunTimeline | null>(null);
  readonly screenshots = input<ReadonlyArray<JobScreenshot>>([]);
  readonly tokenSummary = input<JobTokenSummary | null>(null);
  readonly job = input<JobInfo | null>(null);
  readonly source = input<string>('cli-output.log');
  readonly latestResult = input<string | null>(null);
  readonly initialTab = input<VerboseDebugTab>('overview');
  readonly initialTheme = input<'light' | 'dark'>('dark');

  readonly close = output<void>();
  readonly openTrace = output<RawLineRange>();

  readonly activeTab = signal<VerboseDebugTab>('overview');
  readonly theme = signal<'light' | 'dark'>('dark');

  readonly tabs: Array<{ id: VerboseDebugTab; label: string; icon: string }> = [
    { id: 'overview', label: 'Overview', icon: '📊' },
    { id: 'actors', label: 'Actors', icon: '🎭' },
    { id: 'orchestrator', label: 'Orchestrator', icon: '🛰' },
    { id: 'tools', label: 'Tools', icon: '🛠' },
    { id: 'warnings', label: 'Warnings', icon: '⚠' },
    { id: 'tasks', label: 'Tasks', icon: '🎯' },
    { id: 'tokens', label: 'Tokens', icon: '🪙' },
    { id: 'artifacts', label: 'Artifacts', icon: '🖼' },
    { id: 'trace', label: 'Trace', icon: '📜' }
  ];

  constructor() {
    // Defer initial-input application to a microtask after Angular wires inputs.
    // Using effect would also work, but avoiding the additional import keeps
    // the change footprint small.
    queueMicrotask(() => {
      this.activeTab.set(this.initialTab());
      this.theme.set(this.initialTheme());
    });
  }

  toggleTheme(): void {
    this.theme.set(this.theme() === 'dark' ? 'light' : 'dark');
  }

  emitTrace(range: RawLineRange): void {
    this.openTrace.emit(range);
  }

  // --------------------------------------------------------------------
  // Conversation projection (pure derivation from inputs)
  // --------------------------------------------------------------------

  readonly events = computed<ConversationEvent[]>(() => {
    const lines = this.lines();
    if (!lines.length && !this.runTimeline() && !this.tokenSummary() && !this.screenshots().length) {
      return [];
    }
    const screenshots = this.screenshots().map((s) => ({
      caption: s.caption || s.fileName,
      sourcePath: s.localPath || s.relativePath,
      durablePath: s.relativePath,
      sourceTool: 'screenshot',
      timestamp: s.timestampUtc
    }));
    return projectConversation({
      source: this.source(),
      lines: lines as CliOutputLine[],
      job: this.job(),
      runTimeline: this.runTimeline(),
      tokenSummary: this.tokenSummary(),
      screenshots,
      emitRunMarkers: true,
      emitWorkbenchSummary: true,
      emitWorkbenchPreviews: false,
      emitTraceLink: true,
      emitDebugAggregate: true,
      latestResult: this.latestResult() ?? undefined
    });
  });

  readonly debugEvent = computed<WorkbenchDebugEvent | null>(() => {
    const ev = this.events().find((e): e is WorkbenchDebugEvent => e.kind === 'workbench.debug');
    return ev ?? null;
  });

  readonly orchestratorDecisions = computed(() => {
    return this.events().filter(
      (e): e is Extract<ConversationEvent, { kind: 'decision.orchestrator' }> =>
        e.kind === 'decision.orchestrator'
    );
  });

  readonly toolBursts = computed(() => {
    return this.events().filter(
      (e): e is Extract<ConversationEvent, { kind: 'toolBurst' }> => e.kind === 'toolBurst'
    );
  });

  readonly supervisorWaits = computed(() => {
    return this.events().filter(
      (e): e is Extract<ConversationEvent, { kind: 'supervisor.wait' }> => e.kind === 'supervisor.wait'
    );
  });

  // Stable arrow function for use from the template (Angular templates can't
  // reference arrow type guards inside filter callbacks otherwise).
  readonly supervisorIsResumed = (w: { state: string }) => w.state === 'resumed';

  readonly traceLinkEvents = computed(() => {
    return this.events().filter(
      (e): e is Extract<ConversationEvent, { kind: 'traceLink' }> => e.kind === 'traceLink'
    );
  });

  // --------------------------------------------------------------------
  // Aggregate getters
  // --------------------------------------------------------------------

  readonly toolDensity = computed(() => {
    const d = this.debugEvent();
    if (!d) return { total: 0, failures: 0, families: {} as Partial<Record<ToolFamily, number>> };
    return d.toolDensity;
  });

  readonly warningCounts = computed(() => {
    const d = this.debugEvent();
    if (!d) {
      return {
        supervisorAdvisories: 0,
        parserWarnings: 0,
        captureFails: 0,
        schemaDrifts: 0,
        needsInputLoops: 0,
        watchdogQuiet: 0,
        watchdogKills: 0
      };
    }
    return d.warningCounts;
  });

  readonly runStats = computed(() => {
    const d = this.debugEvent();
    return d?.runStats ?? { runCount: 0, completedCount: 0, failedCount: 0, cancelledCount: 0 };
  });

  readonly runs = computed<RunRecord[]>(() => this.runTimeline()?.runs ?? []);

  readonly totalDurationSeconds = computed(() => {
    return this.runs().reduce((acc, r) => acc + (r.durationSeconds ?? 0), 0);
  });

  readonly activeRunBadge = computed(() => {
    return this.runTimeline()?.hasActiveRun ? 'yes' : 'idle';
  });

  readonly totalTokens = computed(() => {
    const t = this.debugEvent()?.tokenTotals;
    if (!t) return 0;
    return t.inputTokens + t.outputTokens + (t.reasoningTokens ?? 0);
  });

  readonly totalWarnings = computed(() => {
    const w = this.warningCounts();
    return (
      w.parserWarnings +
      w.captureFails +
      w.schemaDrifts +
      w.needsInputLoops +
      w.watchdogQuiet +
      w.watchdogKills
    );
  });

  readonly subtitle = computed(() => {
    const j = this.job();
    if (!j) return 'Read-only diagnostic view';
    return `${j.title} · ${j.state}`;
  });

  readonly taskScopeLabel = computed(() => {
    const j = this.job();
    return j ? j.state : '—';
  });

  readonly jobTokenSummary = computed(() => this.tokenSummary());

  // --------------------------------------------------------------------
  // Per-tab row builders
  // --------------------------------------------------------------------

  readonly actorRows = computed<ActorRow[]>(() => {
    const a = this.debugEvent()?.actorCounts;
    if (!a) return [];
    return [
      { key: 'user', label: 'You', count: a.user, glyph: '🧑' },
      { key: 'taskAgent', label: 'Task agent', count: a.taskAgent, glyph: '🤖' },
      { key: 'orchestrator', label: 'Orchestrator', count: a.orchestrator, glyph: '🛰' },
      { key: 'supervisor', label: 'Supervisor', count: a.supervisor, glyph: '🛡' },
      { key: 'supportingAgent', label: 'Supporting agents', count: a.supportingAgent, glyph: '🧰' }
    ].filter((r) => r.count > 0 || r.key === 'user' || r.key === 'taskAgent');
  });

  readonly maxActorCount = computed(() => {
    return Math.max(1, ...this.actorRows().map((r) => r.count));
  });

  readonly toolRows = computed<ToolRow[]>(() => {
    const families = this.toolDensity().families;
    const failureMap = new Map<ToolFamily, number>();
    for (const burst of this.toolBursts()) {
      // Failure counts are not split per-family in the projection; attribute
      // proportionally to a family by keeping the burst-level total visible
      // as the row's failure count when only one family is involved.
      const keys = Object.keys(burst.families) as ToolFamily[];
      if (keys.length === 1 && burst.failures > 0) {
        const fam = keys[0];
        failureMap.set(fam, (failureMap.get(fam) ?? 0) + burst.failures);
      }
    }
    const entries = Object.entries(families) as [ToolFamily, number][];
    const total = entries.reduce((acc, [, n]) => acc + (n ?? 0), 0);
    return entries
      .filter(([, n]) => (n ?? 0) > 0)
      .sort((a, b) => (b[1] ?? 0) - (a[1] ?? 0))
      .map(([family, count]) => ({
        family,
        label: TOOL_LABELS[family] ?? family,
        count: count ?? 0,
        failures: failureMap.get(family) ?? 0,
        percent: total > 0 ? Math.max(4, Math.round(((count ?? 0) / total) * 100)) : 0
      }));
  });

  readonly warningRows = computed<WarningRow[]>(() => {
    const w = this.warningCounts();
    const tone = (n: number, danger: number, warn: number): 'info' | 'warn' | 'danger' =>
      n >= danger ? 'danger' : n >= warn ? 'warn' : 'info';
    return [
      {
        key: 'parserWarning',
        label: 'Parser warnings',
        count: w.parserWarnings,
        tone: tone(w.parserWarnings, 3, 1),
        description: 'Activity-log parser could not classify a sentinel'
      },
      {
        key: 'captureFail',
        label: 'Session capture-fail',
        count: w.captureFails,
        tone: tone(w.captureFails, 1, 1),
        description: 'CLI session id was not captured; recovery branch fired'
      },
      {
        key: 'schemaDrift',
        label: 'Schema drift',
        count: w.schemaDrifts,
        tone: tone(w.schemaDrifts, 2, 1),
        description: 'Structured Markdown / JSON did not match the expected shape'
      },
      {
        key: 'needsInput',
        label: 'NEEDS_INPUT loops',
        count: w.needsInputLoops,
        tone: tone(w.needsInputLoops, 5, 2),
        description: 'Agent paused for an answer; circuit breaker counts loops'
      },
      {
        key: 'watchdogQuiet',
        label: 'Watchdog quiet windows',
        count: w.watchdogQuiet,
        tone: tone(w.watchdogQuiet, 3, 1),
        description: 'Long quiet stretches noticed by Layer 2 supervisor'
      },
      {
        key: 'watchdogKill',
        label: 'Watchdog kills',
        count: w.watchdogKills,
        tone: tone(w.watchdogKills, 1, 1),
        description: 'Run aborted by the supervisor watchdog'
      }
    ];
  });

  readonly tokenRows = computed<TokenRow[]>(() => {
    const tokenEvents = this.events().filter(
      (e): e is Extract<ConversationEvent, { kind: 'metric.token' }> => e.kind === 'metric.token'
    );
    const grouped = new Map<string, { input: number; output: number }>();
    for (const e of tokenEvents) {
      const key = e.scope ?? 'unknown';
      const prev = grouped.get(key) ?? { input: 0, output: 0 };
      grouped.set(key, {
        input: prev.input + (e.inputTokens ?? 0),
        output: prev.output + (e.outputTokens ?? 0)
      });
    }
    const t = this.tokenSummary();
    if (t && t.totalTokens > 0) {
      const prev = grouped.get('orchestrator') ?? { input: 0, output: 0 };
      grouped.set('orchestrator', {
        input: prev.input + (t.inputTokens ?? 0),
        output: prev.output + (t.outputTokens ?? 0)
      });
    }
    const rows = Array.from(grouped.entries()).map(([scope, v]) => ({
      key: scope,
      scope: this.scopeLabel(scope),
      inputTokens: v.input,
      outputTokens: v.output,
      total: v.input + v.output,
      percent: 0
    }));
    const max = rows.reduce((acc, r) => Math.max(acc, r.total), 0);
    return rows
      .filter((r) => r.total > 0)
      .sort((a, b) => b.total - a.total)
      .map((r) => ({ ...r, percent: max > 0 ? Math.max(6, Math.round((r.total / max) * 100)) : 0 }));
  });

  readonly artifactRows = computed<ArtifactRow[]>(() => {
    return this.screenshots().map<ArtifactRow>((s) => ({
      caption: s.caption || s.fileName,
      durablePath: s.relativePath,
      sourcePath: s.localPath,
      url: s.url,
      status: s.status,
      timestamp: s.timestampUtc
    }));
  });

  readonly traceLinkRows = computed<TraceLinkRow[]>(() => {
    const rows: TraceLinkRow[] = [];
    const debug = this.debugEvent();
    if (debug) {
      for (const link of debug.traceLinks) {
        rows.push({
          label: link.label ?? `${link.range.start}-${link.range.end}`,
          start: link.range.start,
          end: link.range.end,
          range: link.range,
          kind: link.label?.split(' ')[0] ?? 'event'
        });
      }
    }
    for (const link of this.traceLinkEvents()) {
      rows.push({
        label: link.label,
        start: link.link.range.start,
        end: link.link.range.end,
        range: link.link.range,
        kind: link.target
      });
    }
    return rows.slice(0, 80);
  });

  readonly overviewBands = computed(() => {
    const tools = this.toolDensity().total;
    const tokens = this.totalTokens();
    const warnings = this.totalWarnings();
    const screenshots = this.artifactRows().length;
    const orchestrator = this.orchestratorDecisions().length;
    const max = Math.max(1, tools, tokens / 100, warnings, screenshots, orchestrator);
    const bands = [
      { name: 'Tool density', value: `${tools} call${tools === 1 ? '' : 's'}`, percent: bandPercent(tools, max) },
      { name: 'Tokens', value: fmtTokens(tokens), percent: bandPercent(tokens / 100, max) },
      { name: 'Warnings', value: `${warnings}`, percent: bandPercent(warnings, max) },
      { name: 'Artifacts', value: `${screenshots}`, percent: bandPercent(screenshots, max) },
      { name: 'Orchestrator', value: `${orchestrator}`, percent: bandPercent(orchestrator, max) }
    ];
    return bands.filter((b) => b.percent > 0 || b.name === 'Tokens' || b.name === 'Tool density');
  });

  // --------------------------------------------------------------------
  // Tab badges + small helpers
  // --------------------------------------------------------------------

  tabBadge(id: VerboseDebugTab): string | null {
    switch (id) {
      case 'orchestrator': {
        const n = this.orchestratorDecisions().length;
        return n > 0 ? `${n}` : null;
      }
      case 'tools': {
        const n = this.toolDensity().total;
        return n > 0 ? `${n}` : null;
      }
      case 'warnings': {
        const n = this.totalWarnings();
        return n > 0 ? `${n}` : null;
      }
      case 'tasks': {
        const n = this.runs().length;
        return n > 0 ? `${n}` : null;
      }
      case 'artifacts': {
        const n = this.artifactRows().length;
        return n > 0 ? `${n}` : null;
      }
      default:
        return null;
    }
  }

  rowPercent(value: number, max: number): number {
    if (max <= 0) return 0;
    return Math.max(4, Math.round((value / max) * 100));
  }

  formatTokens(n: number): string { return fmtTokens(n); }

  formatTime(ts: string): string {
    if (!ts) return '';
    try {
      return new Date(ts).toLocaleTimeString();
    } catch {
      return ts;
    }
  }

  formatDuration(seconds: number): string {
    if (!seconds || seconds <= 0) return '0s';
    if (seconds < 60) return `${Math.round(seconds)}s`;
    const m = Math.floor(seconds / 60);
    const s = Math.round(seconds % 60);
    if (m < 60) return s === 0 ? `${m}m` : `${m}m ${s}s`;
    const h = Math.floor(m / 60);
    const min = m % 60;
    return min === 0 ? `${h}h` : `${h}h ${min}m`;
  }

  traceSource(): string { return this.source(); }

  private scopeLabel(scope: string): string {
    switch (scope) {
      case 'task': return 'Task agent';
      case 'orchestrator': return 'Orchestrator';
      case 'run': return 'Latest run';
      case 'project': return 'Project';
      case 'supporting-agent': return 'Supporting agents';
      default: return scope;
    }
  }
}

function bandPercent(value: number, max: number): number {
  if (max <= 0 || value <= 0) return 0;
  return Math.max(4, Math.min(100, Math.round((value / max) * 100)));
}
