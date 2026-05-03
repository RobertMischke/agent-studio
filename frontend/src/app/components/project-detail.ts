import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { JobService } from '../services/job.service';
import { GroupedJobs, OrchestratorLogEntry, OrchestratorSession, RunnerStatus } from '../models/job.model';
import { OrchestratorRunner_KnownModels } from './project-detail.models';
import { TokenSummaryBlockComponent } from './token-summary-block';
import { GlobalOrchestratorCardComponent } from './global-orchestrator-card';
import { ProjectSecuritySectionComponent } from './project-security-section';
import { ProjectArchitectureSectionComponent } from './project-architecture-section';

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
    ProjectArchitectureSectionComponent
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
        <h3>Auto-commit</h3>
        <label class="proj-detail__toggle">
          <input type="checkbox"
                 data-testid="project-detail-auto-commit"
                 [(ngModel)]="autoCommitDraft"
                 (ngModelChange)="onAutoCommitChange()">
          <span>Auto-commit on transition <code>3-progress → 4-review</code></span>
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

      <app-project-security-section [projectName]="projectName()" />

      <app-project-architecture-section [projectName]="projectName()" />

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

  private readonly jobService = inject(JobService);

  readonly settings = signal<ProjectSettingsRow | null>(null);
  readonly runnerStatus = signal<RunnerStatus | null>(null);
  readonly grouped = signal<GroupedJobs | null>(null);
  readonly recentEntries = signal<OrchestratorLogEntry[]>([]);
  readonly orchSession = signal<OrchestratorSession | null>(null);

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
      { state: '1-preparation', label: 'Preparation', count: c(grouped.preparation) },
      { state: '2-ready',       label: 'Ready',       count: c(grouped.ready) },
      { state: '3-progress',    label: 'Progress',    count: c(grouped.progress) },
      { state: '4-review',      label: 'Review',      count: c(grouped.review) },
      { state: '5-completed',   label: 'Completed',   count: c(grouped.completed) },
      { state: '6-archive',     label: 'Archive',     count: c(grouped.archive) }
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
