import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  effect,
  inject,
  input,
  signal
} from '@angular/core';
import { JobService } from '../../services/job.service';
import { OrchestratorLogEntry } from '../../models/job.model';
import { ChatComponent } from '../chat/chat.component';
import { ChatMessage, ChatSubmitEvent } from '../chat/chat-types';

/**
 * Right-hand side sheet that hosts the orchestrator chat. The shell follows
 * the same flex-collapse pattern as `cli-usage-sheet` (host width animates
 * to 0 when closed so the board reflows instead of being overlaid).
 *
 * Phase 2 wires the existing read-only orchestrator log into a chat-style
 * timeline backed by `<app-chat>`. Project switching at the top mirrors the
 * board's project-tabs metaphor: the user can flip between per-project
 * orchestrator threads without losing context. Sending a message is wired
 * to the existing `overrideOrchestratorEntry` endpoint as a best-effort
 * "steer" path until Phase 3 adds a proper conversation endpoint.
 */
@Component({
  selector: 'app-orchestrator-side-sheet',
  standalone: true,
  imports: [ChatComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <aside class="sheet" [class.sheet--open]="open()" data-testid="orch-side-sheet">
      <header class="sheet__header">
        <div class="sheet__title-block">
          <span class="sheet__eyebrow">Orchestrator</span>
          <h2 class="sheet__title">Project chat</h2>
        </div>
        <div class="sheet__header-actions">
          <button class="sheet__btn"
                  (click)="refresh()"
                  [disabled]="loading()"
                  title="Reload entries"
                  data-testid="orch-side-sheet-refresh">
            {{ loading() ? '⏳' : '↻' }}
          </button>
          <button class="sheet__close"
                  type="button"
                  (click)="hide()"
                  title="Close panel"
                  data-testid="orch-side-sheet-close">✕</button>
        </div>
      </header>

      @if (projects().length > 1) {
        <nav class="sheet__tabs" data-testid="orch-side-sheet-tabs">
          @for (proj of projects(); track proj) {
            <button class="sheet__tab"
                    [class.sheet__tab--active]="proj === activeProject()"
                    [attr.data-testid]="'orch-side-sheet-tab-' + proj"
                    (click)="setActiveProject(proj)">
              {{ proj }}
            </button>
          }
        </nav>
      } @else if (projects().length === 1) {
        <div class="sheet__only-project" data-testid="orch-side-sheet-single-project">
          {{ activeProject() }}
        </div>
      }

      @if (errorMsg(); as err) {
        <div class="sheet__error">{{ err }}</div>
      }

      <div class="sheet__chat">
        @if (activeProject(); as proj) {
          <app-chat
            variant="embedded"
            [messages]="messages()"
            [pending]="sending()"
            [disabled]="sending() || !activeJobId()"
            [placeholder]="composerPlaceholder()"
            [emptyState]="emptyStateText()"
            (submitMessage)="onSubmit($event)" />
        } @else {
          <div class="sheet__no-project">No watched projects yet.</div>
        }
      </div>
    </aside>
  `,
  styles: [`
    /* The host participates in flex layout (same as cli-usage-sheet). When
       closed it collapses to zero width so the main board reclaims the
       space; when open it animates to a comfortable reading width. */
    :host {
      display: block;
      width: 0;
      transition: width 0.22s ease;
      overflow: hidden;
      flex: 0 0 auto;
    }
    :host(.is-open) { width: min(460px, 92vw); }

    .sheet {
      width: min(460px, 92vw);
      height: 100%;
      background: #11111b;
      border-left: 1px solid rgba(255,255,255,0.08);
      display: flex;
      flex-direction: column;
      color: #e2e8f0;
    }
    .sheet__header {
      display: flex;
      justify-content: space-between;
      align-items: flex-start;
      padding: 14px 16px 12px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      background:
        radial-gradient(circle at top left, rgba(139, 92, 246, 0.16), transparent 60%),
        rgba(255,255,255,0.02);
    }
    .sheet__title-block { display: flex; flex-direction: column; gap: 2px; }
    .sheet__eyebrow {
      font-size: 11px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: #c4b5fd;
      font-weight: 700;
    }
    .sheet__title {
      margin: 0;
      font-size: 16px;
      color: #f8fafc;
    }
    .sheet__header-actions { display: flex; gap: 6px; align-items: center; }
    .sheet__btn {
      background: rgba(255,255,255,0.06);
      border: 1px solid rgba(255,255,255,0.08);
      color: #cbd5e1;
      padding: 4px 10px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 12px;
    }
    .sheet__btn:hover:not(:disabled) { background: rgba(255,255,255,0.10); }
    .sheet__btn:disabled { opacity: 0.5; cursor: progress; }
    .sheet__close {
      background: rgba(255,255,255,0.06);
      border: 0;
      color: #cbd5e1;
      width: 28px; height: 28px;
      border-radius: 999px;
      cursor: pointer;
    }
    .sheet__close:hover { background: rgba(255,255,255,0.12); }

    .sheet__tabs {
      display: flex;
      gap: 4px;
      padding: 8px 10px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      overflow-x: auto;
      scrollbar-width: thin;
    }
    .sheet__tab {
      flex: 0 0 auto;
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.08);
      color: #94a3b8;
      padding: 4px 12px;
      border-radius: 999px;
      font-size: 12px;
      cursor: pointer;
      white-space: nowrap;
      transition: background 0.15s, color 0.15s, border-color 0.15s;
    }
    .sheet__tab:hover {
      background: rgba(255,255,255,0.08);
      color: #cbd5e1;
    }
    .sheet__tab--active {
      background: rgba(124,58,237,0.28);
      color: #ede9fe;
      border-color: rgba(196,181,253,0.55);
    }
    .sheet__only-project {
      padding: 8px 14px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      font-size: 12px;
      color: #94a3b8;
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }

    .sheet__error {
      margin: 10px 14px 0;
      padding: 8px 12px;
      background: rgba(244,63,94,0.1);
      border: 1px solid rgba(244,63,94,0.18);
      color: #fda4af;
      border-radius: 8px;
      font-size: 12px;
    }

    .sheet__chat {
      flex: 1 1 auto;
      min-height: 0;
      display: flex;
      flex-direction: column;
      padding: 10px 12px 12px;
    }
    .sheet__no-project {
      padding: 28px 12px;
      color: #64748b;
      text-align: center;
      font-style: italic;
    }
  `],
  host: {
    '[class.is-open]': 'open()'
  }
})
export class OrchestratorSideSheetComponent implements OnInit, OnDestroy {
  /**
   * Names of all watched projects. The side sheet uses these for the
   * project switcher and falls back to the first one when nothing else
   * is selected.
   */
  readonly projects = input<string[]>([]);
  /**
   * Optional preferred project (typically the project of the currently
   * open task detail or the user's last toggled project). When this
   * changes externally the sheet aligns itself, so opening a task and
   * then opening the orchestrator picks the right thread without an
   * extra click.
   */
  readonly preferredProject = input<string | null>(null);

  readonly open = signal(false);
  readonly activeProject = signal<string | null>(null);
  readonly entries = signal<OrchestratorLogEntry[]>([]);
  readonly loading = signal(false);
  readonly sending = signal(false);
  readonly errorMsg = signal<string | null>(null);
  /**
   * Last "decision" entry that referenced a job. New user messages are
   * wired to the existing override endpoint, which needs an originalTs +
   * jobId pair to anchor the steer. Phase 3 will replace this with a
   * proper conversation endpoint.
   */
  readonly anchorEntry = signal<OrchestratorLogEntry | null>(null);
  /**
   * Locally-buffered turns the user has sent in this session. They are
   * appended to the rendered chat so the user sees their own message
   * immediately, before the next refresh pulls the orchestrator's
   * intervention entry.
   */
  private readonly userTurns = signal<ChatMessage[]>([]);

  private readonly jobService = inject(JobService);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  readonly activeJobId = computed<string | null>(() => this.anchorEntry()?.jobId ?? null);

  /**
   * Convert the orchestrator log into chat messages. Decisions and
   * interventions render as orchestrator turns; observations and actions
   * render as system turns (they are background events the orchestrator
   * narrated about itself, not direct messages to the user). User
   * overrides become user turns inserted just before their target entry.
   */
  readonly messages = computed<ChatMessage[]>(() => {
    const entries = this.entries();
    const out: ChatMessage[] = [];
    for (const entry of entries) {
      if (entry.userOverride) {
        out.push({
          id: `override:${entry.ts}`,
          role: 'user',
          text: entry.userOverride.newDirection,
          timestamp: entry.userOverride.at
        });
      }
      // Every log entry is the orchestrator's voice (decisions,
      // observations, actions, interventions) — keep them on the
      // 'orchestrator' role so the chat reads as one speaker. Kind shows
      // up in the bold headline at the top of the bubble.
      out.push({
        id: `entry:${entry.ts}:${entry.kind}`,
        role: 'orchestrator',
        text: this.formatEntry(entry),
        timestamp: entry.ts
      });
    }
    for (const turn of this.userTurns()) out.push(turn);
    return out;
  });

  readonly composerPlaceholder = computed(() => {
    const anchor = this.anchorEntry();
    if (anchor?.jobId) {
      return `Steer ${anchor.jobId}: tell the orchestrator what to do differently…`;
    }
    return 'No anchor decision yet — sending will be enabled after the next orchestrator decision.';
  });

  readonly emptyStateText = computed(() => {
    if (this.loading()) return 'Loading…';
    return 'No orchestrator activity yet for this project.';
  });

  constructor() {
    /**
     * When the active project changes (or the sheet opens), refetch and
     * reset the locally-buffered user turns so we don't carry chatter
     * from one project's thread into another.
     */
    effect(() => {
      this.activeProject();
      this.open();
      if (this.open() && this.activeProject()) {
        this.userTurns.set([]);
        this.refresh(false);
      }
    });

    /**
     * Track the host's preferred project. If the user opens a different
     * task, the sheet realigns automatically.
     */
    effect(() => {
      const preferred = this.preferredProject();
      const projects = this.projects();
      if (!preferred) {
        if (this.activeProject() == null && projects.length > 0) {
          this.activeProject.set(projects[0]);
        }
        return;
      }
      if (projects.includes(preferred) && preferred !== this.activeProject()) {
        this.activeProject.set(preferred);
      }
    });
  }

  ngOnInit(): void {
    this.pollTimer = setInterval(() => {
      if (this.open() && this.activeProject() && !this.loading()) {
        this.refresh(true);
      }
    }, 10_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  show(): void {
    this.open.set(true);
  }

  hide(): void {
    this.open.set(false);
  }

  toggle(): void {
    this.open() ? this.hide() : this.show();
  }

  setActiveProject(proj: string): void {
    if (proj === this.activeProject()) return;
    this.activeProject.set(proj);
  }

  refresh(silent = false): void {
    const proj = this.activeProject();
    if (!proj) return;
    if (!silent) this.loading.set(true);
    this.jobService.getOrchestratorLog(proj).subscribe({
      next: (resp) => {
        const entries = resp.entries ?? [];
        this.entries.set(entries);
        this.errorMsg.set(null);
        const lastDecision = [...entries].reverse().find(
          (e) => e.kind === 'decision' && !!e.jobId
        );
        this.anchorEntry.set(lastDecision ?? null);
        if (!silent) this.loading.set(false);
      },
      error: (err) => {
        const message = err?.error?.error || err?.message || 'Failed to load orchestrator log';
        this.errorMsg.set(message);
        if (!silent) this.loading.set(false);
      }
    });
  }

  onSubmit(event: ChatSubmitEvent): void {
    const proj = this.activeProject();
    const anchor = this.anchorEntry();
    if (!proj || !anchor?.jobId) return;
    const text = event.text.trim();
    if (!text) return;

    const localId = `local:${Date.now()}`;
    this.userTurns.update((curr) => [
      ...curr,
      {
        id: localId,
        role: 'user',
        text,
        timestamp: new Date().toISOString(),
        pending: true
      }
    ]);
    this.sending.set(true);

    this.jobService.overrideOrchestratorEntry(proj, {
      originalTs: anchor.ts,
      jobId: anchor.jobId,
      newDirection: text
    }).subscribe({
      next: () => {
        this.sending.set(false);
        this.userTurns.update((curr) =>
          curr.map((t) => (t.id === localId ? { ...t, pending: false } : t))
        );
        this.refresh(true);
      },
      error: (err) => {
        this.sending.set(false);
        const message = err?.error?.error || err?.message || 'Failed to send';
        this.userTurns.update((curr) =>
          curr.map((t) => (t.id === localId ? { ...t, pending: false, error: message } : t))
        );
      }
    });
  }

  private formatEntry(entry: OrchestratorLogEntry): string {
    const head = `**${capitalize(entry.kind)} · ${entry.topic}**`;
    const body = entry.summary || '';
    const reasoning = entry.reasoning ? `\n\n_${entry.reasoning}_` : '';
    return `${head}\n\n${body}${reasoning}`.trim();
  }
}

function capitalize(s: string): string {
  return s.charAt(0).toUpperCase() + s.slice(1);
}
