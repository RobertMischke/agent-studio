import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input, output, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { OrchestratorLogEntry } from '../models/job.model';
import { JobService } from '../services/job.service';

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
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <section class="orch-chat" data-testid="orchestrator-feed">
      <header class="orch-chat__head">
        <div class="orch-chat__brand">
          <span class="orch-chat__avatar">⚙</span>
          <div>
            <h2 class="orch-chat__title">Orchestrator</h2>
            <p class="orch-chat__sub">Runbook · canonical session</p>
          </div>
        </div>
        <div class="orch-chat__actions" aria-label="Chat actions">
          <button class="orch-chat__icon" (click)="refresh()" [disabled]="loading()" data-testid="orchestrator-refresh" title="Refresh">
            {{ loading() ? '…' : '↻' }}
          </button>
          <button class="orch-chat__icon" title="Session log">▤</button>
          <button class="orch-chat__icon" title="Archive">▭</button>
          <button class="orch-chat__icon" (click)="close.emit()" title="Close">×</button>
        </div>
      </header>

      <nav class="orch-chat__scope" aria-label="Orchestrator scope">
        <button class="orch-chat__scope-btn orch-chat__scope-btn--active" type="button">▱ Project</button>
        <button class="orch-chat__scope-btn" type="button">▦ Global</button>
        <span class="orch-chat__memory">● memory v9 · fresh {{ memoryAgeLabel() }}</span>
      </nav>

      @if (error()) {
        <div class="orch-chat__error">{{ error() }}</div>
      }

      <main class="orch-chat__body">
        @if (entries().length === 0 && !loading() && !error()) {
          <div class="orch-chat__empty">
            <span class="orch-chat__empty-icon">▤</span>
            <p>No project chat events yet.</p>
            <small>{{ projectName() }} · waiting for the canonical session</small>
          </div>
        }
        @for (entry of reversed(); track entry.ts + entry.summary) {
          @if (isEvent(entry)) {
            <article class="orch-chat__event"
                     [class.orch-chat__event--action]="entry.kind === 'action'"
                     [class.orch-chat__event--observation]="entry.kind === 'observation'">
              <span class="orch-chat__event-icon">{{ eventIcon(entry) }}</span>
              <span>{{ entry.summary }}</span>
              <time>{{ formatTime(entry.ts) }}</time>
            </article>
          } @else {
            <article class="orch-chat__turn"
                     [class.orch-chat__turn--user]="entry.kind === 'intervention'"
                     [class.orch-chat__turn--orchestrator]="entry.kind !== 'intervention'">
              <div class="orch-chat__person">
                <span class="orch-chat__person-avatar">{{ avatarText(entry) }}</span>
                <strong>{{ authorLabel(entry) }}</strong>
                <time>{{ formatTime(entry.ts) }}</time>
              </div>
              <div class="orch-chat__bubble">
                <p>{{ entry.summary }}</p>
                @if (entry.reasoning) {
                  <details class="orch-chat__details">
                    <summary>Reasoning</summary>
                    <p>{{ entry.reasoning }}</p>
                  </details>
                }
                <div class="orch-chat__chips">
                  <span class="orch-chat__chip"># {{ entry.topic || kindLabel(entry.kind) }}</span>
                  @if (entry.jobId) {
                    <span class="orch-chat__chip">task {{ entry.jobId }}</span>
                  }
                  @if (entry.tokenUsage; as tu) {
                    <span class="orch-chat__chip" [title]="tokenTooltip(tu)">
                      {{ tu.model || 'model' }} · ↑{{ tu.inputTokens }} ↓{{ tu.outputTokens }}
                    </span>
                  }
                </div>
              </div>
              @if (entry.kind === 'decision' && entry.jobId) {
                @if (overridingTs() === entry.ts) {
                  <div class="orch-chat__override-form">
                    <textarea class="orch-chat__override-input"
                              placeholder="Your direction. Will be sent as a Steer follow-up."
                              [(ngModel)]="overrideDraft"
                              data-testid="orchestrator-override-input"
                              rows="3"></textarea>
                    <div class="orch-chat__override-actions">
                      <button class="orch-chat__mini-btn"
                              (click)="cancelOverride()"
                              [disabled]="submittingOverride()">Cancel</button>
                      <button class="orch-chat__mini-btn orch-chat__mini-btn--primary"
                              (click)="submitOverride(entry)"
                              [disabled]="submittingOverride() || !overrideDraft.trim()"
                              data-testid="orchestrator-override-submit">
                        {{ submittingOverride() ? 'Sending...' : 'Send override' }}
                      </button>
                    </div>
                  </div>
                } @else {
                  <button class="orch-chat__override"
                          (click)="startOverride(entry)"
                          data-testid="orchestrator-override-start"
                          title="Disagree with this decision? Send a Steer follow-up to the agent.">
                    Steer this decision
                  </button>
                }
              }
            </article>
          }
        }
      </main>

      <footer class="orch-chat__composer">
        <div class="orch-chat__composer-tools">
          <button type="button">☰ #</button>
          <button type="button">⇄ thread</button>
          <span>routing: Codex (Claude paused)</span>
        </div>
        <textarea [(ngModel)]="composerDraft"
                  placeholder="Ask the project orchestrator..."
                  rows="3"
                  data-testid="orchestrator-composer"></textarea>
        <div class="orch-chat__composer-bottom">
          <span>↵ send · / command</span>
          <button class="orch-chat__task-btn" type="button">/task</button>
          <button class="orch-chat__send" type="button" [disabled]="!composerDraft.trim()">↗ Send</button>
        </div>
      </footer>
    </section>
  `,
  styles: [`
    :host {
      display: block;
      height: 100%;
      color: #1d2430;
      font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
    }
    button,
    textarea {
      font: inherit;
    }
    .orch-chat {
      display: grid;
      grid-template-rows: auto auto minmax(0, 1fr) auto;
      height: 100%;
      background: #f7f5f2;
    }
    .orch-chat__head {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 18px;
      padding: 26px 28px 16px;
      border-bottom: 1px solid rgba(15, 23, 42, 0.06);
    }
    .orch-chat__brand {
      display: flex;
      align-items: center;
      gap: 16px;
      min-width: 0;
    }
    .orch-chat__avatar {
      display: inline-grid;
      place-items: center;
      width: 40px;
      height: 40px;
      border-radius: 11px;
      background: #f26a3d;
      color: #101827;
      font-size: 19px;
      font-weight: 900;
      flex: 0 0 auto;
    }
    .orch-chat__title {
      margin: 0;
      color: #0f172a;
      font-size: 21px;
      line-height: 1.1;
      font-weight: 760;
      letter-spacing: 0;
    }
    .orch-chat__sub {
      margin: 4px 0 0;
      color: #9ca3af;
      font-size: 15px;
      line-height: 1.25;
    }
    .orch-chat__actions {
      display: flex;
      align-items: center;
      gap: 18px;
      color: #475569;
    }
    .orch-chat__icon {
      display: inline-grid;
      place-items: center;
      width: 28px;
      height: 28px;
      border: 0;
      border-radius: 8px;
      background: transparent;
      color: #334155;
      cursor: pointer;
      font-size: 20px;
      line-height: 1;
    }
    .orch-chat__icon:hover:not(:disabled) {
      background: rgba(15, 23, 42, 0.06);
    }
    .orch-chat__icon:disabled {
      opacity: 0.45;
      cursor: progress;
    }
    .orch-chat__scope {
      display: flex;
      align-items: center;
      gap: 10px;
      padding: 10px 22px;
      border-bottom: 1px solid rgba(15, 23, 42, 0.06);
      background: rgba(255, 255, 255, 0.38);
    }
    .orch-chat__scope-btn {
      min-height: 31px;
      padding: 5px 13px;
      border: 1px solid transparent;
      border-radius: 7px;
      background: transparent;
      color: #64748b;
      cursor: pointer;
    }
    .orch-chat__scope-btn--active {
      border-color: rgba(242, 106, 61, 0.22);
      background: rgba(242, 106, 61, 0.08);
      color: #172033;
    }
    .orch-chat__memory {
      margin-left: auto;
      color: #9ca3af;
      font-size: 15px;
      white-space: nowrap;
    }
    .orch-chat__memory::first-letter {
      color: #10b981;
    }
    .orch-chat__error {
      margin: 10px 22px 0;
      padding: 9px 11px;
      border: 1px solid rgba(248, 113, 113, 0.30);
      border-radius: 12px;
      background: rgba(254, 226, 226, 0.80);
      color: #991b1b;
    }
    .orch-chat__body {
      min-height: 0;
      overflow-y: auto;
      padding: 20px 20px 18px;
      display: flex;
      flex-direction: column;
      gap: 14px;
      scrollbar-color: rgba(148, 163, 184, 0.7) transparent;
    }
    .orch-chat__empty {
      margin: auto;
      text-align: center;
      color: #94a3b8;
    }
    .orch-chat__empty-icon {
      display: inline-grid;
      place-items: center;
      width: 42px;
      height: 42px;
      margin-bottom: 8px;
      border-radius: 50%;
      background: rgba(15, 23, 42, 0.05);
      color: #64748b;
    }
    .orch-chat__empty p {
      margin: 0;
      color: #475569;
      font-weight: 700;
    }
    .orch-chat__empty small {
      display: block;
      margin-top: 4px;
      color: #94a3b8;
    }
    .orch-chat__event {
      display: grid;
      grid-template-columns: 18px minmax(0, 1fr) auto;
      gap: 10px;
      align-items: center;
      padding: 1px 10px;
      color: #475569;
      font-size: 15px;
    }
    .orch-chat__event-icon {
      color: #d97706;
      font-weight: 900;
    }
    .orch-chat__event--observation .orch-chat__event-icon {
      color: #0f7df2;
    }
    .orch-chat__event time,
    .orch-chat__person time {
      color: #a8a29e;
      font-variant-numeric: tabular-nums;
      white-space: nowrap;
    }
    .orch-chat__turn {
      display: grid;
      gap: 7px;
    }
    .orch-chat__person {
      display: grid;
      grid-template-columns: 36px minmax(0, 1fr) auto;
      align-items: center;
      gap: 9px;
      color: #0f172a;
    }
    .orch-chat__person-avatar {
      display: inline-grid;
      place-items: center;
      width: 31px;
      height: 31px;
      border-radius: 50%;
      background: #0f7df2;
      color: #fff;
      font-size: 12px;
      font-weight: 800;
    }
    .orch-chat__turn--user .orch-chat__person-avatar {
      background: #df7b54;
      color: #111827;
    }
    .orch-chat__turn--orchestrator .orch-chat__person-avatar {
      background: #d777d4;
    }
    .orch-chat__bubble {
      margin-left: 45px;
      padding: 14px 18px;
      border: 1px solid rgba(15, 125, 242, 0.14);
      border-radius: 11px;
      background: #eef3f8;
      color: #111827;
      box-shadow: 0 1px 0 rgba(15, 23, 42, 0.03);
    }
    .orch-chat__turn--user .orch-chat__bubble {
      border-color: rgba(242, 106, 61, 0.18);
      background: #fff1ec;
    }
    .orch-chat__turn--orchestrator .orch-chat__bubble {
      border-color: rgba(209, 101, 207, 0.18);
      background: #fff7ff;
    }
    .orch-chat__bubble p {
      margin: 0;
      font-size: 18px;
      line-height: 1.55;
      white-space: pre-wrap;
    }
    .orch-chat__details {
      margin-top: 10px;
      color: #475569;
      font-size: 14px;
    }
    .orch-chat__details summary {
      cursor: pointer;
      font-weight: 700;
    }
    .orch-chat__details p {
      margin-top: 6px;
      font-size: 14px;
      color: #475569;
    }
    .orch-chat__chips {
      display: flex;
      flex-wrap: wrap;
      gap: 7px;
      margin-top: 12px;
    }
    .orch-chat__chip {
      display: inline-flex;
      align-items: center;
      min-height: 24px;
      padding: 2px 8px;
      border: 1px solid rgba(15, 23, 42, 0.08);
      border-radius: 999px;
      background: rgba(255, 255, 255, 0.52);
      color: #78716c;
      font-size: 14px;
    }
    .orch-chat__override {
      justify-self: start;
      margin-left: 45px;
      border: 1px solid rgba(242, 106, 61, 0.18);
      border-radius: 999px;
      background: rgba(255, 255, 255, 0.72);
      color: #f26a3d;
      padding: 4px 10px;
      cursor: pointer;
      font-size: 13px;
    }
    .orch-chat__override:hover {
      background: #fff7ed;
    }
    .orch-chat__override-form {
      margin-left: 45px;
      padding: 10px;
      border: 1px solid rgba(242, 106, 61, 0.22);
      border-radius: 12px;
      background: #fff7ed;
    }
    .orch-chat__override-input,
    .orch-chat__composer textarea {
      width: 100%;
      box-sizing: border-box;
      border: 1px solid rgba(15, 23, 42, 0.08);
      border-radius: 8px;
      background: rgba(255, 255, 255, 0.84);
      color: #111827;
      padding: 9px 11px;
      font-family: inherit;
      font-size: 14px;
      resize: vertical;
    }
    .orch-chat__override-input:focus,
    .orch-chat__composer textarea:focus {
      outline: none;
      border-color: rgba(242, 106, 61, 0.40);
      box-shadow: 0 0 0 3px rgba(242, 106, 61, 0.10);
    }
    .orch-chat__override-actions {
      display: flex;
      gap: 8px;
      justify-content: flex-end;
      margin-top: 8px;
    }
    .orch-chat__mini-btn,
    .orch-chat__task-btn,
    .orch-chat__send,
    .orch-chat__composer-tools button {
      border: 1px solid rgba(15, 23, 42, 0.08);
      border-radius: 8px;
      background: rgba(255, 255, 255, 0.72);
      color: #64748b;
      padding: 5px 10px;
      cursor: pointer;
      font-size: 14px;
    }
    .orch-chat__mini-btn--primary,
    .orch-chat__send {
      background: #f26a3d;
      border-color: #f26a3d;
      color: #fff;
      font-weight: 700;
    }
    .orch-chat__mini-btn:disabled,
    .orch-chat__send:disabled {
      opacity: 0.45;
      cursor: not-allowed;
    }
    .orch-chat__composer {
      padding: 10px 20px 16px;
      border-top: 1px solid rgba(15, 23, 42, 0.07);
      background: rgba(247, 245, 242, 0.94);
    }
    .orch-chat__composer-tools,
    .orch-chat__composer-bottom {
      display: flex;
      align-items: center;
      gap: 9px;
      color: #a8a29e;
      font-size: 14px;
    }
    .orch-chat__composer-tools {
      margin-bottom: 8px;
    }
    .orch-chat__composer-tools span {
      margin-left: auto;
    }
    .orch-chat__composer textarea {
      min-height: 82px;
      max-height: 130px;
      font-size: 18px;
    }
    .orch-chat__composer-bottom {
      margin-top: 8px;
    }
    .orch-chat__task-btn {
      margin-left: auto;
      color: #111827;
    }
    @media (max-width: 720px) {
      .orch-chat__head {
        padding: 18px 16px 12px;
      }
      .orch-chat__actions {
        gap: 6px;
      }
      .orch-chat__scope {
        padding: 8px 14px;
        flex-wrap: wrap;
      }
      .orch-chat__memory {
        width: 100%;
        margin-left: 0;
      }
      .orch-chat__bubble,
      .orch-chat__override,
      .orch-chat__override-form {
        margin-left: 0;
      }
      .orch-chat__bubble p,
      .orch-chat__composer textarea {
        font-size: 16px;
      }
    }
  `]
})
export class OrchestratorFeedComponent implements OnInit, OnDestroy {
  readonly projectName = input.required<string>();
  readonly close = output<void>();

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
  composerDraft = '';
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

  isEvent(entry: OrchestratorLogEntry): boolean {
    return entry.kind === 'action' || entry.kind === 'observation';
  }

  eventIcon(entry: OrchestratorLogEntry): string {
    return entry.kind === 'action' ? '◔' : '▤';
  }

  authorLabel(entry: OrchestratorLogEntry): string {
    return entry.kind === 'intervention' ? 'You' : 'Orchestrator';
  }

  avatarText(entry: OrchestratorLogEntry): string {
    return entry.kind === 'intervention' ? 'YO' : 'OR';
  }

  memoryAgeLabel(): string {
    const newest = this.entries()
      .map(e => new Date(e.ts).getTime())
      .filter(t => !Number.isNaN(t))
      .sort((a, b) => b - a)[0];
    if (!newest) return 'now';
    const minutes = Math.max(0, Math.round((Date.now() - newest) / 60_000));
    if (minutes < 1) return 'now';
    if (minutes < 60) return `${minutes}m`;
    return `${Math.round(minutes / 60)}h`;
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
