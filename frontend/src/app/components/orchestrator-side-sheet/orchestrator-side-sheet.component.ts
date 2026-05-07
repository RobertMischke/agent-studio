import {
  ChangeDetectionStrategy,
  Component,
  OnDestroy,
  OnInit,
  computed,
  effect,
  inject,
  input,
  output,
  signal,
  untracked,
  viewChild,
} from '@angular/core';
import { JobService } from '../../services/job.service';
import { OrchestratorChatTurn, WatchPathEntry } from '../../models/job.model';
import { ChatComponent } from '../chat/chat.component';
import { ChatEvent, ChatMessage, ChatSubmitEvent } from '../chat/chat-types';
import { RoadmapIntakePanelComponent } from '../roadmap-intake/roadmap-intake-panel.component';
import { ProjectChatListComponent } from '../project-chat-list/project-chat-list.component';

/**
 * Right-hand side sheet that hosts the orchestrator chat. Shell follows
 * the same flex-collapse pattern as `cli-usage-sheet` (host width animates
 * to 0 when closed so the board reflows instead of being overlaid).
 *
 * Phase 3 wires this to a real bidirectional conversation endpoint
 * (`/api/runner/{project}/orchestrator-chat`): the backend resumes the
 * singleton global Claude session, persists both user and orchestrator
 * turns under `<watchPath>/.orchestrator/orchestrator-chat.jsonl`, and
 * returns the reply turn. Project switching at the top mirrors the
 * board's project-tabs metaphor; threads are independent per project.
 *
 * The composer also emits a `createTaskFromDraft` event (Phase 5) so the
 * host can pre-fill the create-task dialog with the user's draft text and
 * pasted screenshots without reaching back into the chat component.
 */
@Component({
  selector: 'app-orchestrator-side-sheet',
  standalone: true,
  imports: [ChatComponent, RoadmapIntakePanelComponent, ProjectChatListComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <aside class="sheet" [class.sheet--open]="open()" data-testid="orch-side-sheet">
      <header class="sheet__header">
        <div class="sheet__title-block">
          <span class="sheet__eyebrow">Orchestrator</span>
          <h2 class="sheet__title">Project chat</h2>
        </div>
        <div class="sheet__header-actions">
          @if (activeJobId() && activeWatchPath()) {
            <button class="sheet__btn"
                    type="button"
                    (click)="onOpenVerboseDebug()"
                    [title]="'Open the read-only Verbose Debug view for ' + (activeJobTitle() ?? 'the active task')"
                    data-testid="orch-side-sheet-verbose-debug">🐞</button>
          }
          <button class="sheet__btn"
                  (click)="refresh()"
                  [disabled]="loading()"
                  title="Reload chat history"
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

      <nav class="sheet__tabs" data-testid="orch-side-sheet-tabs">
        <div class="sheet__combo-wrap"
             [class.sheet__combo-wrap--active]="mode() === 'project'"
             [class.sheet__combo-wrap--open]="comboOpen()"
             data-testid="orch-side-sheet-project-combo-wrap">
          <span class="sheet__select-icon">💬</span>
          <input #comboInput
                 type="text"
                 class="sheet__combo-input"
                 data-testid="orch-side-sheet-project-combo"
                 [value]="comboQuery()"
                 [placeholder]="activeProject() ?? 'Pick a project…'"
                 (input)="onComboInput($event)"
                 (focus)="onComboFocus()"
                 (blur)="onComboBlur()"
                 (keydown)="onComboKeydown($event)" />
          <span class="sheet__select-caret">▾</span>
          @if (comboOpen() && filteredProjects().length > 0) {
            <ul class="sheet__combo-list"
                role="listbox"
                data-testid="orch-side-sheet-project-combo-list">
              @for (proj of filteredProjects(); track proj; let i = $index) {
                <li class="sheet__combo-option"
                    role="option"
                    [class.sheet__combo-option--active]="i === comboHighlight()"
                    [class.sheet__combo-option--current]="proj === activeProject()"
                    [attr.data-testid]="'orch-side-sheet-project-combo-option-' + proj"
                    (mousedown)="onComboOptionMousedown($event)"
                    (click)="selectComboOption(proj, $event)">
                  {{ proj }}
                </li>
              }
            </ul>
          }
        </div>
        @if (activeJobId() && activeJobTitle()) {
          <button class="sheet__tab sheet__tab--task"
                  [class.sheet__tab--active]="mode() === 'task'"
                  data-testid="orch-side-sheet-tab-task"
                  (click)="selectTaskTab()"
                  [title]="activeJobTitle() ?? ''">
            🎯 {{ activeJobTitle() }}
          </button>
        }
        <button class="sheet__tab sheet__tab--intake"
                [class.sheet__tab--active]="mode() === 'intake'"
                data-testid="orch-side-sheet-tab-intake"
                (click)="selectIntakeTab()"
                [disabled]="!activeProject()"
                title="Send a long dump to the roadmap splitter">
          🗺 Roadmap
        </button>
      </nav>
      <select hidden
              data-testid="orch-side-sheet-project-select"
              [value]="activeProject() ?? ''"
              (change)="onProjectSelectChange($event)">
        @for (proj of projects(); track proj) {
          <option [value]="proj">{{ proj }}</option>
        }
      </select>

      @if (errorMsg(); as err) {
        <div class="sheet__error">{{ err }}</div>
      }

      <div class="sheet__chat">
        @if (mode() === 'intake' && activeProject()) {
          <app-roadmap-intake-panel
            [activeWatchPath]="activeWatchPathForIntake()"
            [projectName]="activeProject()"
            (created)="onIntakeCreated($event)" />
        } @else if (mode() === 'task' && activeJobId()) {
          <app-chat
            variant="embedded"
            [messages]="taskMessages()"
            [pending]="taskSending()"
            [disabled]="taskSending()"
            [placeholder]="'Send a follow-up to this task (Continue mode: Steer)…'"
            [emptyState]="'No follow-ups sent from here yet. The full activity log lives in the protocol pane.'"
            (submitMessage)="onTaskSubmit($event)" />
        } @else if (activeProject()) {
          @if (virtualChatEnabled()) {
            <app-project-chat-list
              #projectChatList
              [project]="activeProject()" />
            <app-chat
              variant="embedded"
              [messages]="[]"
              [events]="[]"
              [pending]="sending()"
              [disabled]="sending()"
              [placeholder]="'Ask the orchestrator about this project…'"
              [emptyState]="''"
              [bodyMaxHeight]="'0px'"
              (submitMessage)="onSubmit($event)" />
          } @else {
            <app-chat
              variant="embedded"
              [messages]="messages()"
              [events]="events()"
              [pending]="sending()"
              [disabled]="sending()"
              [placeholder]="'Ask the orchestrator about this project…'"
              [emptyState]="emptyStateText()"
              (submitMessage)="onSubmit($event)" />
          }

          <div class="sheet__draft-actions" data-testid="orch-side-sheet-draft-actions">
            <button class="sheet__draft-btn sheet__draft-btn--primary"
                    type="button"
                    (click)="onCreateTaskFromYourMessage()"
                    [disabled]="!canCreateTaskFromUserMessage()"
                    title="Open Add Task pre-filled with the text you typed (works even if the orchestrator hasn't replied yet)"
                    data-testid="orch-side-sheet-make-task-from-yours">
              ✦ Make a task from your message
            </button>
            <button class="sheet__draft-btn"
                    type="button"
                    (click)="onCreateTaskFromLastReply()"
                    [disabled]="!canCreateTaskFromReply()"
                    title="Open Add Task pre-filled with the last orchestrator reply"
                    data-testid="orch-side-sheet-make-task">
              ✨ Make a task from this reply
            </button>
          </div>
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
      padding: 12px 14px 10px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      background:
        radial-gradient(circle at top left, rgba(139, 92, 246, 0.16), transparent 60%),
        rgba(255,255,255,0.02);
    }
    .sheet__title-block { display: flex; flex-direction: column; gap: 2px; }
    .sheet__eyebrow {
      font-size: 10px;
      letter-spacing: 0.08em;
      text-transform: uppercase;
      color: #c4b5fd;
      font-weight: 700;
    }
    .sheet__title {
      margin: 0;
      font-size: 15px;
      color: #f8fafc;
      font-weight: 600;
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
      width: 26px; height: 26px;
      border-radius: 999px;
      cursor: pointer;
      font-size: 13px;
    }
    .sheet__close:hover { background: rgba(255,255,255,0.12); }

    .sheet__tabs {
      display: flex;
      gap: 6px;
      padding: 6px 10px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      align-items: center;
      flex-wrap: nowrap;
      min-width: 0;
    }
    /* Project picker is a real <select> so it scales past pills/tabs to
       ~10+ projects without overflow. The wrap renders our pill chrome
       around the native control; the active tab indicator still glows. */
    .sheet__select-wrap {
      flex: 1 1 auto;
      min-width: 0;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 3px 10px 3px 11px;
      background: linear-gradient(135deg, rgba(124,58,237,0.55), rgba(99,102,241,0.55));
      border: 1px solid rgba(196,181,253,0.95);
      border-radius: 999px;
      color: #ffffff;
      font-weight: 600;
      font-size: 12px;
      cursor: pointer;
      box-shadow: 0 0 0 1px rgba(196,181,253,0.35), 0 1px 4px rgba(99,102,241,0.35);
      transition: opacity 0.15s;
    }
    .sheet__select-wrap:not(.sheet__select-wrap--active) {
      background: rgba(255,255,255,0.04);
      border-color: rgba(255,255,255,0.08);
      box-shadow: none;
      color: #cbd5e1;
      font-weight: 500;
    }
    .sheet__select-icon { flex: 0 0 auto; opacity: 0.85; }
    .sheet__select {
      flex: 1 1 auto;
      min-width: 0;
      background: transparent;
      border: 0;
      color: inherit;
      font: inherit;
      cursor: pointer;
      padding: 0;
      appearance: none;
      -webkit-appearance: none;
      text-overflow: ellipsis;
      white-space: nowrap;
      overflow: hidden;
    }
    .sheet__select:focus { outline: none; }
    .sheet__select option {
      color: #0f172a;
      background: #f8fafc;
    }
    .sheet__select-caret { flex: 0 0 auto; opacity: 0.85; font-size: 10px; }

    /* Searchable combobox: pill chrome around an editable input plus a
       floating list. Lets the user filter ~10+ projects by typing instead
       of scanning a flat dropdown. The native <select> stays in the DOM
       (hidden) for accessibility/test continuity. */
    .sheet__combo-wrap {
      position: relative;
      flex: 1 1 auto;
      min-width: 0;
      display: inline-flex;
      align-items: center;
      gap: 6px;
      padding: 3px 10px 3px 11px;
      background: linear-gradient(135deg, rgba(124,58,237,0.55), rgba(99,102,241,0.55));
      border: 1px solid rgba(196,181,253,0.95);
      border-radius: 999px;
      color: #ffffff;
      font-weight: 600;
      font-size: 12px;
      box-shadow: 0 0 0 1px rgba(196,181,253,0.35), 0 1px 4px rgba(99,102,241,0.35);
      transition: opacity 0.15s, border-color 0.15s;
    }
    .sheet__combo-wrap:not(.sheet__combo-wrap--active) {
      background: rgba(255,255,255,0.04);
      border-color: rgba(255,255,255,0.08);
      box-shadow: none;
      color: #cbd5e1;
      font-weight: 500;
    }
    .sheet__combo-wrap--open {
      border-color: rgba(196,181,253,1);
      box-shadow: 0 0 0 1px rgba(196,181,253,0.5), 0 4px 14px rgba(15,23,42,0.55);
    }
    .sheet__combo-input {
      flex: 1 1 auto;
      min-width: 0;
      background: transparent;
      border: 0;
      color: inherit;
      font: inherit;
      padding: 0;
      text-overflow: ellipsis;
      white-space: nowrap;
      overflow: hidden;
    }
    .sheet__combo-input::placeholder {
      color: inherit;
      opacity: 0.85;
    }
    .sheet__combo-input:focus { outline: none; }
    .sheet__combo-list {
      position: absolute;
      top: calc(100% + 6px);
      left: 0;
      right: 0;
      max-height: 280px;
      overflow-y: auto;
      margin: 0;
      padding: 4px;
      list-style: none;
      background: #0f172a;
      border: 1px solid rgba(196,181,253,0.35);
      border-radius: 12px;
      box-shadow: 0 8px 24px rgba(2,6,23,0.55);
      z-index: 30;
    }
    .sheet__combo-option {
      padding: 6px 10px;
      border-radius: 8px;
      color: #cbd5e1;
      font-weight: 500;
      cursor: pointer;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      transition: background 0.1s, color 0.1s;
    }
    .sheet__combo-option:hover,
    .sheet__combo-option--active {
      background: rgba(124,58,237,0.35);
      color: #ffffff;
    }
    .sheet__combo-option--current {
      color: #c4b5fd;
      font-weight: 600;
    }
    .sheet__combo-option--current::before {
      content: '✓ ';
      opacity: 0.8;
    }
    .sheet__tab {
      flex: 0 0 auto;
      max-width: 60%;
      background: rgba(255,255,255,0.04);
      border: 1px solid rgba(255,255,255,0.08);
      color: #94a3b8;
      padding: 3px 11px;
      border-radius: 999px;
      font-size: 12px;
      cursor: pointer;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
      transition: background 0.15s, color 0.15s, border-color 0.15s;
    }
    .sheet__tab:hover {
      background: rgba(255,255,255,0.08);
      color: #cbd5e1;
    }
    .sheet__tab--active {
      background: linear-gradient(135deg, rgba(124,58,237,0.55), rgba(99,102,241,0.55));
      color: #ffffff;
      border-color: rgba(196,181,253,0.95);
      font-weight: 600;
      box-shadow: 0 0 0 1px rgba(196,181,253,0.35), 0 1px 4px rgba(99,102,241,0.35);
    }
    .sheet__tab--task {
      background: rgba(20,184,166,0.10);
      border-color: rgba(94,234,212,0.30);
      color: #a7f3d0;
    }
    .sheet__tab--task.sheet__tab--active {
      background: linear-gradient(135deg, rgba(20,184,166,0.55), rgba(13,148,136,0.55));
      border-color: rgba(94,234,212,0.95);
      color: #ffffff;
      box-shadow: 0 0 0 1px rgba(94,234,212,0.35), 0 1px 4px rgba(13,148,136,0.35);
    }
    .sheet__tab--intake {
      background: rgba(217,119,6,0.10);
      border-color: rgba(252,211,77,0.30);
      color: #fde68a;
    }
    .sheet__tab--intake.sheet__tab--active {
      background: linear-gradient(135deg, rgba(234,88,12,0.55), rgba(217,119,6,0.55));
      border-color: rgba(252,211,77,0.95);
      color: #ffffff;
      box-shadow: 0 0 0 1px rgba(252,211,77,0.35), 0 1px 4px rgba(217,119,6,0.35);
    }
    .sheet__tab--intake:disabled {
      opacity: 0.4;
      cursor: not-allowed;
    }
    .sheet__only-project {
      padding: 6px 14px;
      border-bottom: 1px solid rgba(255,255,255,0.06);
      font-size: 11px;
      color: #94a3b8;
      letter-spacing: 0.04em;
      text-transform: uppercase;
    }

    .sheet__error {
      margin: 8px 12px 0;
      padding: 6px 10px;
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
      gap: 8px;
    }
    .sheet__no-project {
      padding: 28px 12px;
      color: #64748b;
      text-align: center;
      font-style: italic;
    }
    .sheet__draft-actions {
      display: flex;
      justify-content: flex-end;
      gap: 6px;
    }
    .sheet__draft-btn {
      background: rgba(139,92,246,0.16);
      border: 1px solid rgba(167,139,250,0.45);
      color: #ddd6fe;
      padding: 5px 12px;
      border-radius: 8px;
      cursor: pointer;
      font-size: 12px;
      font-weight: 500;
      transition: background 0.15s, color 0.15s, border-color 0.15s;
    }
    .sheet__draft-btn:hover:not(:disabled) {
      background: rgba(139,92,246,0.28);
      color: #f5f3ff;
      border-color: rgba(196,181,253,0.7);
    }
    .sheet__draft-btn:disabled {
      opacity: 0.4;
      cursor: not-allowed;
    }
    .sheet__draft-btn--primary {
      background: rgba(59,130,246,0.18);
      border-color: rgba(96,165,250,0.55);
      color: #bfdbfe;
    }
    .sheet__draft-btn--primary:hover:not(:disabled) {
      background: rgba(59,130,246,0.30);
      color: #eff6ff;
      border-color: rgba(147,197,253,0.7);
    }
  `],
  host: {
    '[class.is-open]': 'open()'
  }
})
export class OrchestratorSideSheetComponent implements OnInit, OnDestroy {
  readonly projects = input<string[]>([]);
  readonly preferredProject = input<string | null>(null);
  /**
   * Full watch path entries so the roadmap-intake panel can resolve the
   * active project name to the on-disk path the backend needs.
   */
  readonly watchPaths = input<WatchPathEntry[]>([]);

  /**
   * Phase 6 inputs: when a task detail is open, the host passes the
   * job id, title, and watch path so the side sheet can offer an
   * "Active task" tab whose chat sends Continue (mode: Steer)
   * follow-ups to that specific task. All three are required together;
   * the tab only shows when both id and title are non-empty.
   */
  readonly activeJobId = input<string | null>(null);
  readonly activeJobTitle = input<string | null>(null);
  readonly activeWatchPath = input<string | null>(null);

  /**
   * Phase 5: when the user clicks "Make a task from this reply", the
   * host opens the create-task dialog pre-filled with the orchestrator's
   * latest reply text. The host wires the dialog; this component just
   * names what to seed it with.
   */
  readonly createTaskFromDraft = output<{ projectName: string; promptText: string }>();

  /**
   * Phase: Verbose Debug. Emitted when the user clicks the bug icon in the
   * sheet header while a task tab is in scope. The host (app shell) opens
   * the read-only Verbose Debug overlay against the active task by fetching
   * its evidence (cli output, run timeline, screenshots) and feeding the
   * shared `<app-verbose-debug-overlay>` component.
   */
  readonly openVerboseDebug = output<{ jobId: string; watchPath: string; jobTitle: string | null }>();

  readonly open = signal(false);
  readonly activeProject = signal<string | null>(null);
  readonly turns = signal<OrchestratorChatTurn[]>([]);
  readonly loading = signal(false);
  readonly sending = signal(false);
  readonly errorMsg = signal<string | null>(null);

  /**
   * Slice D virtualised chat list. Off by default so the existing
   * non-virtualised surface (and its Playwright coverage) stays as-is
   * while the new endpoints + index settle. The flag is read once at
   * construction; reload to flip it.
   */
  readonly virtualChatEnabled = signal<boolean>(this.readVirtualFlag());

  private readonly projectChatList = viewChild<ProjectChatListComponent>('projectChatList');

  /** Read the `?virtualChat=1` URL flag once at construction. */
  private readVirtualFlag(): boolean {
    if (typeof window === 'undefined') return false;
    try {
      return new URLSearchParams(window.location.search).get('virtualChat') === '1';
    } catch {
      return false;
    }
  }

  /**
   * Which surface is in front: the project orchestrator thread, the
   * active task's Continue-mode follow-up chat, or the roadmap intake
   * panel that splits a long dump into reviewable task drafts.
   */
  readonly mode = signal<'project' | 'task' | 'intake'>('project');

  /**
   * Resolve the active project name to its on-disk watch path so the
   * roadmap-intake panel can call the backend without re-fetching the
   * watch-paths list on its own.
   */
  readonly activeWatchPathForIntake = computed<string | null>(() => {
    const name = this.activeProject();
    if (!name) return null;
    const entry = this.watchPaths().find((wp) => wp.name === name);
    return entry?.path ?? null;
  });

  /**
   * Per-task Continue-mode chat state. The history here is local-only
   * (the activity-log-view in the protocol pane is the durable record
   * of the run); this signal just holds the user's follow-ups and the
   * acknowledgements so the side sheet stays a real chat surface even
   * when the user is steering a task.
   */
  readonly taskMessages = signal<ChatMessage[]>([]);
  readonly taskSending = signal(false);

  /**
   * Searchable project combobox state. The plain list-style picker did
   * not scale well past a handful of projects; with ~10 watched
   * workspaces the user wants to filter by typing. The native <select>
   * remains in the DOM (hidden) for accessibility and existing tests.
   */
  readonly comboOpen = signal(false);
  readonly comboQuery = signal('');
  readonly comboHighlight = signal(0);

  readonly filteredProjects = computed<string[]>(() => {
    const q = this.comboQuery().trim().toLowerCase();
    const all = this.projects();
    if (!q) return all;
    return all.filter((p) => p.toLowerCase().includes(q));
  });

  /** Locally-buffered user turns shown immediately (server is source of truth on next refresh). */
  private readonly localTurns = signal<OrchestratorChatTurn[]>([]);

  /**
   * Inline event cards interleaved with the chat (Slice B mechanism).
   * Default empty; the data source for the six event kinds (tool-call,
   * watchdog, rate-limit, decision, update, task) lands as a separate
   * task that wires SignalR / endpoint streams into this signal.
   * The `?demoEvents=1` URL flag seeds three sample events for visual
   * review and Playwright regression coverage of the rendering contract.
   */
  readonly events = signal<ChatEvent[]>([]);

  private readonly jobService = inject(JobService);
  private pollTimer: ReturnType<typeof setInterval> | null = null;

  /**
   * Convert chat turns into the chat-component's message shape. Failed
   * orchestrator turns surface the error in the bubble's footer instead
   * of dropping the (typically empty) reply silently.
   */
  readonly messages = computed<ChatMessage[]>(() => {
    const proj = this.activeProject();
    const merged = [...this.turns(), ...this.localTurns()];
    return merged.map<ChatMessage>((t) => ({
      id: t.id,
      role: t.role,
      text: t.text,
      timestamp: t.ts,
      pending: !!(t as { pending?: boolean }).pending,
      error: t.errorMessage ?? undefined,
      attachments: (t.attachments ?? []).map((a) => ({
        alt: a.alt,
        // Server returns "chat-attachments/<file>"; resolve through the
        // GET endpoint so the <img> in the bubble actually loads.
        url: this.resolveAttachmentUrl(proj, a.relativePath)
      }))
    }));
  });

  private resolveAttachmentUrl(projectName: string | null, relativePath: string): string {
    if (!projectName || !relativePath) return relativePath;
    const fileName = relativePath.startsWith('chat-attachments/')
      ? relativePath.substring('chat-attachments/'.length)
      : relativePath;
    return `/api/runner/${encodeURIComponent(projectName)}/orchestrator-chat/attachments/${encodeURIComponent(fileName)}`;
  }

  readonly emptyStateText = computed(() => {
    if (this.loading()) return 'Loading…';
    return 'No conversation yet. Ask the orchestrator about this project.';
  });

  readonly canCreateTaskFromReply = computed(() => {
    const last = [...this.turns()].reverse().find(
      (t) => t.role === 'orchestrator' && !!t.text && !t.errorMessage
    );
    return !!last;
  });

  /**
   * "Make a task from your message" gate. Independent of the orchestrator
   * reply so the user can convert their own typed intent into a task even
   * when the orchestrator round-trip is slow, errors, or gets dropped on
   * the floor by a backend hiccup. Looks at the merged turn list (server +
   * locally-buffered pending user turn) so the button is reachable the
   * instant the message is submitted.
   *
   * Why this exists: before this affordance, the only path from chat to
   * task was "Make a task from this reply", which is gated on a non-empty
   * non-error orchestrator turn. A failed or pending reply silently
   * stranded the user's intent - the typed message was visible but
   * un-actionable. This button gives the user a deterministic exit.
   */
  readonly canCreateTaskFromUserMessage = computed(() => {
    const merged = [...this.turns(), ...this.localTurns()];
    const last = [...merged].reverse().find(
      (t) => t.role === 'user' && !!t.text && t.text.trim().length > 0
    );
    return !!last;
  });

  constructor() {
    effect(() => {
      this.activeProject();
      this.open();
      if (this.open() && this.activeProject()) {
        this.localTurns.set([]);
        this.refresh(false);
      }
    });

    /**
     * Sync activeProject from the host's preferred project. We `untracked`
     * the read of activeProject so this effect only fires when preferred
     * or the project list changes — otherwise picking a project in the
     * combobox would re-trigger the effect and snap the selection back
     * to the host's preferred project on every user pick.
     */
    effect(() => {
      const preferred = this.preferredProject();
      const projects = this.projects();
      untracked(() => {
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
    });

    /**
     * If the host closes the task detail, fall back to the project
     * thread so the side sheet doesn't sit on a now-invalid task tab.
     * Switching tasks also clears the per-task local chat — we don't
     * want one task's follow-ups bleeding into the next.
     */
    effect(() => {
      const id = this.activeJobId();
      if (!id) {
        if (this.mode() === 'task') this.mode.set('project');
        if (this.taskMessages().length > 0) this.taskMessages.set([]);
        return;
      }
    });
  }

  ngOnInit(): void {
    // Slow poll: the chat history only changes when the user sends a
    // message (which we already refresh after) or when something else
    // appends a turn. 30s keeps the UI honest without burning quota.
    this.pollTimer = setInterval(() => {
      if (this.open() && this.activeProject() && !this.loading() && !this.sending()) {
        this.refresh(true);
      }
    }, 30_000);

    this.maybeSeedDemoEvents();
  }

  /**
   * `?demoEvents=1` on the URL seeds three sample event cards so the
   * Slice B rendering contract can be reviewed visually and pinned by
   * Playwright before the live data source lands. No-op without the
   * flag, so production callers see an empty events list as designed.
   */
  private maybeSeedDemoEvents(): void {
    if (typeof window === 'undefined') return;
    const params = new URLSearchParams(window.location.search);
    if (params.get('demoEvents') !== '1') return;
    const baseTs = Date.now();
    const iso = (offsetMs: number) => new Date(baseTs + offsetMs).toISOString();
    this.events.set([
      {
        id: 'demo-tool-call-1',
        kind: 'tool-call',
        timestamp: iso(0),
        summary: 'Read backend/Services/Runner/PhaseAwareWatchdog.cs',
        detail:
          '```\n'
          + '/* Result: 412 lines, last modified 2026-05-04 */\n'
          + 'PhaseAwareWatchdog observes per-phase silence budgets;\n'
          + 'FormatBudgetReason emits a one-line summary plus the\n'
          + 'previous CLI event that preceded the silence so the\n'
          + 'operator can see what the agent was doing.\n'
          + '```'
      },
      {
        id: 'demo-watchdog-1',
        kind: 'watchdog',
        timestamp: iso(45_000),
        severity: 'warn',
        summary: 'Tool burst phase silent for 90s (budget: 60s)',
        detail:
          '**Phase:** tool-burst\n\n**Silence:** 90s\n\n**Budget:** 60s\n\n'
          + 'Last event before the silence:\n\n'
          + '```\n'
          + '● Read frontend/src/app/components/chat/chat.component.ts\n'
          + '  L1-100\n'
          + '```'
      },
      {
        id: 'demo-rate-limit-1',
        kind: 'rate-limit',
        timestamp: iso(90_000),
        severity: 'warn',
        summary: 'Anthropic 5h window: 78% used, resets in 1h 12m',
        detail:
          '```\n'
          + '{\n'
          + '  "type": "rate_limit_event",\n'
          + '  "window": "5h",\n'
          + '  "used_pct": 78,\n'
          + '  "reset_at": "2026-05-06T13:12:00Z"\n'
          + '}\n'
          + '```'
      }
    ]);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearInterval(this.pollTimer);
    this.pollTimer = null;
  }

  show(): void { this.open.set(true); }
  hide(): void { this.open.set(false); }
  toggle(): void { this.open() ? this.hide() : this.show(); }

  setActiveProject(proj: string): void {
    if (proj === this.activeProject()) return;
    this.activeProject.set(proj);
  }

  selectProjectTab(proj: string): void {
    this.mode.set('project');
    this.setActiveProject(proj);
  }

  onProjectSelectChange(event: Event): void {
    const value = (event.target as HTMLSelectElement | null)?.value ?? '';
    if (!value) return;
    this.selectProjectTab(value);
  }

  onComboFocus(): void {
    this.comboOpen.set(true);
    this.comboHighlight.set(0);
  }

  onComboBlur(): void {
    // Defer so a click on a list option (mousedown -> mouseup -> blur) still
    // registers before we tear the list down.
    setTimeout(() => {
      this.comboOpen.set(false);
      this.comboQuery.set('');
    }, 120);
  }

  onComboInput(event: Event): void {
    const value = (event.target as HTMLInputElement | null)?.value ?? '';
    this.comboQuery.set(value);
    this.comboOpen.set(true);
    this.comboHighlight.set(0);
  }

  onComboKeydown(event: KeyboardEvent): void {
    const list = this.filteredProjects();
    if (event.key === 'ArrowDown') {
      event.preventDefault();
      if (list.length === 0) return;
      this.comboOpen.set(true);
      this.comboHighlight.update((i) => (i + 1) % list.length);
    } else if (event.key === 'ArrowUp') {
      event.preventDefault();
      if (list.length === 0) return;
      this.comboOpen.set(true);
      this.comboHighlight.update((i) => (i - 1 + list.length) % list.length);
    } else if (event.key === 'Enter') {
      event.preventDefault();
      const pick = list[this.comboHighlight()] ?? list[0];
      if (pick) this.commitComboSelection(pick);
    } else if (event.key === 'Escape') {
      event.preventDefault();
      this.comboOpen.set(false);
      this.comboQuery.set('');
      (event.target as HTMLInputElement | null)?.blur();
    }
  }

  /**
   * Block the input's blur on mousedown so the click handler still has
   * a live list to bind to. Without this the blur fires between
   * mousedown and click and the option is gone before the click lands.
   */
  onComboOptionMousedown(event: MouseEvent): void {
    event.preventDefault();
  }

  selectComboOption(proj: string, event: MouseEvent): void {
    event.preventDefault();
    this.commitComboSelection(proj);
  }

  private commitComboSelection(proj: string): void {
    this.selectProjectTab(proj);
    this.comboOpen.set(false);
    this.comboQuery.set('');
  }

  selectTaskTab(): void {
    if (!this.activeJobId()) return;
    this.mode.set('task');
  }

  selectIntakeTab(): void {
    if (!this.activeProject()) return;
    this.mode.set('intake');
  }

  onOpenVerboseDebug(): void {
    const jobId = this.activeJobId();
    const watchPath = this.activeWatchPath();
    if (!jobId || !watchPath) return;
    this.openVerboseDebug.emit({ jobId, watchPath, jobTitle: this.activeJobTitle() });
  }

  /**
   * Refresh the board so the newly-created drafts appear in
   * `1-preparation`. We don't switch tabs - the intake panel surfaces
   * its own "drafts created" confirmation.
   */
  onIntakeCreated(_event: { count: number }): void {
    this.jobService.refresh(true);
  }

  /**
   * Phase 6: send a follow-up to the currently open task via the
   * existing Continue endpoint (Steer mode). The reply does not stream
   * back here — it lands in the protocol pane's activity log — so we
   * append a synthetic system acknowledgement that points the user to
   * the right surface.
   */
  onTaskSubmit(event: ChatSubmitEvent): void {
    const jobId = this.activeJobId();
    const watchPath = this.activeWatchPath();
    if (!jobId || !watchPath) return;
    const text = event.text.trim();
    if (!text) return;

    const userId = `task-user:${Date.now()}`;
    this.taskMessages.update((curr) => [
      ...curr,
      {
        id: userId,
        role: 'user',
        text,
        timestamp: new Date().toISOString(),
        pending: true
      }
    ]);
    this.taskSending.set(true);

    this.jobService.continueJob(jobId, text, watchPath, undefined, undefined, 'steer').subscribe({
      next: () => {
        this.taskSending.set(false);
        this.taskMessages.update((curr) =>
          curr.map((m) => (m.id === userId ? { ...m, pending: false } : m))
        );
        this.taskMessages.update((curr) => [
          ...curr,
          {
            id: `task-ack:${Date.now()}`,
            role: 'system',
            text: 'Follow-up queued in **Steer** mode. The agent\'s reply streams into the protocol pane\'s activity log.',
            timestamp: new Date().toISOString()
          }
        ]);
        for (const att of event.attachments) URL.revokeObjectURL(att.previewUrl);
      },
      error: (err) => {
        this.taskSending.set(false);
        const message = err?.error?.error || err?.message || 'Failed to send follow-up';
        this.taskMessages.update((curr) =>
          curr.map((m) =>
            m.id === userId ? { ...m, pending: false, error: message } : m
          )
        );
      }
    });
  }

  refresh(silent = false): void {
    const proj = this.activeProject();
    if (!proj) return;
    if (!silent) this.loading.set(true);
    this.jobService.getOrchestratorChat(proj).subscribe({
      next: (resp) => {
        this.turns.set(resp.turns ?? []);
        this.errorMsg.set(null);
        if (!silent) this.loading.set(false);
      },
      error: (err) => {
        const message = err?.error?.error || err?.message || 'Failed to load orchestrator chat';
        this.errorMsg.set(message);
        if (!silent) this.loading.set(false);
      }
    });
  }

  async onSubmit(event: ChatSubmitEvent): Promise<void> {
    const proj = this.activeProject();
    if (!proj) return;
    const text = event.text.trim();
    if (!text && event.attachments.length === 0) return;

    // Render the user's turn immediately so the chat doesn't sit silent
    // while the orchestrator thinks (Opus replies often take 30-60s).
    const localId = `local:${Date.now()}`;
    const localTurn: OrchestratorChatTurn & { pending?: boolean } = {
      id: localId,
      ts: new Date().toISOString(),
      role: 'user',
      text: text || (event.attachments.length > 0 ? '(attachments)' : ''),
      pending: true
    };
    this.localTurns.update((curr) => [...curr, localTurn]);
    this.sending.set(true);

    // Upload each pasted/dropped image first so the chat message can
    // reference real files. We do this sequentially to keep error
    // surfaces simple and the frontend code small; orchestrator chats
    // rarely carry more than 1-2 images per turn.
    let uploaded: { alt: string; relativePath: string }[] = [];
    try {
      for (const att of event.attachments) {
        const resp = await this.uploadOne(proj, att.file);
        uploaded.push({ alt: att.alt, relativePath: resp.relativePath });
      }
    } catch (err) {
      this.sending.set(false);
      const message = (err as { message?: string })?.message ?? 'Attachment upload failed';
      this.localTurns.update((curr) =>
        curr.map((t) => (t.id === localId ? { ...t, pending: false, errorMessage: message } : t))
      );
      return;
    }

    this.jobService.sendOrchestratorChat(proj, {
      text: text || (uploaded.length > 0 ? '(attachments)' : ''),
      attachments: uploaded.length > 0 ? uploaded : undefined
    }).subscribe({
      next: () => {
        this.sending.set(false);
        this.localTurns.set([]);
        this.refresh(true);
        // Slice D virtualised list: pull the new turn(s) from disk via
        // /scroll. Cheap (one ranged query) and keeps the windowed
        // renderer in sync without us having to mirror the OrchestratorChat
        // append logic on the client side.
        this.projectChatList()?.resetAndLoad();
        for (const att of event.attachments) URL.revokeObjectURL(att.previewUrl);
      },
      error: (err) => {
        this.sending.set(false);
        const message = err?.error?.error || err?.message || 'Failed to send';
        this.localTurns.update((curr) =>
          curr.map((t) => (t.id === localId ? { ...t, pending: false, errorMessage: message } : t))
        );
      }
    });
  }

  private uploadOne(projectName: string, file: File): Promise<{ relativePath: string; url: string }> {
    return new Promise((resolve, reject) => {
      this.jobService.uploadOrchestratorChatAttachment(projectName, file).subscribe({
        next: (resp) => resolve({ relativePath: resp.relativePath, url: resp.url }),
        error: (err) => reject(new Error(err?.error?.error || err?.message || 'Upload failed'))
      });
    });
  }

  /**
   * Phase 5: hand the latest orchestrator reply back to the host so it
   * can open the create-task dialog pre-filled with that text.
   */
  onCreateTaskFromLastReply(): void {
    const proj = this.activeProject();
    if (!proj) return;
    const last = [...this.turns()].reverse().find(
      (t) => t.role === 'orchestrator' && !!t.text && !t.errorMessage
    );
    if (!last) return;
    this.createTaskFromDraft.emit({ projectName: proj, promptText: last.text });
  }

  /**
   * Open the create-task dialog seeded with the user's most-recent typed
   * message - the deterministic escape hatch for "I described a task in
   * the chat and the orchestrator never replied / errored". Looks at
   * server turns first, then the locally-buffered pending turn, so the
   * affordance works the moment a message is submitted.
   */
  onCreateTaskFromYourMessage(): void {
    const proj = this.activeProject();
    if (!proj) return;
    const merged = [...this.turns(), ...this.localTurns()];
    const last = [...merged].reverse().find(
      (t) => t.role === 'user' && !!t.text && t.text.trim().length > 0
    );
    if (!last) return;
    this.createTaskFromDraft.emit({ projectName: proj, promptText: last.text });
  }

}
