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
} from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { WatchPathEntry } from '../../../../models/task.model';
import { TaskState } from '../../../../models/task.model';
import type {
  ChatExecutionContext,
  ComposerLocationContext,
  OrchestratorChatTurn,
  OrchestratorContextSession,
} from '../../../../features/orchestrator';
import { buildChatNavigationContext } from '../../../../features/orchestrator';
import { ChatComponent } from 'coding-agent-chat/composer';
import { ConversationViewComponent } from 'coding-agent-chat/conversation';
import {
  ChatEvent,
  ChatModelSelection,
  ChatSubmitEvent,
  ChatToolbarItem,
} from 'coding-agent-chat/core';
import { SidesheetComponent } from '../../../../components/sidesheet/sidesheet.component';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { OrchestratorContextHeaderComponent } from '../orchestrator-context-header/orchestrator-context-header.component';
import { ChatSwitcherRailComponent } from '../chat-switcher-rail/chat-switcher-rail.component';
import { OrchestratorProjectPickerComponent } from '../orchestrator-project-picker/orchestrator-project-picker.component';
import { OrchestratorPanelStateService } from '../../state/orchestrator-panel-state.service';
import { OrchestratorContextDigestService } from '../../state/orchestrator-context-digest.service';
import { OrchestratorComposerModelService } from '../../state/orchestrator-composer-model.service';
import {
  parseBugHashtags,
  resolveAttachmentUrl,
  readFileAsBase64,
  buildDemoEvents,
  buildOrchestratorConversationEvents,
  sameOrchestratorChatTurns,
} from './orchestrator-side-sheet.util';
/**
 * Push-layout side sheet hosting automatic context-keyed orchestrator chats.
 * The reusable composer owns chat interaction; this host owns app context,
 * transcript endpoints, and the ORCH-1 read digest.
 */
@Component({
  selector: 'app-orchestrator-side-sheet',
  standalone: true,
  imports: [
    ChatComponent,
    ConversationViewComponent,
    AppTooltipDirective,
    SidesheetComponent,
    OrchestratorContextHeaderComponent,
    ChatSwitcherRailComponent,
    OrchestratorProjectPickerComponent
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-side-sheet.component.html',
  styleUrl: './orchestrator-side-sheet.component.scss',
  providers: [OrchestratorContextDigestService],
  host: {
    '[class.is-open]': 'open()',
    // When open, drive the host width from the persisted user choice so
    // the resize handle can grow / shrink the panel live. When closed the
    // binding evaluates to null and the static `:host { width: 0 }` rule
    // from the .scss wins, so the close transition still works.
    '[style.width.px]': 'open() ? panelWidth() : null'
  }
})
export class OrchestratorSideSheetComponent implements OnInit, OnDestroy {
  readonly jobService = inject(TaskService);
  readonly composerModel = inject(OrchestratorComposerModelService);
  readonly projects = input<string[]>([]);
  readonly preferredProject = input<string | null>(null);
  readonly watchPaths = input<WatchPathEntry[]>([]);
  /**
   * Canonical active-tab context, derived by Studio and rendered unchanged in
   * the composer's standard footer (via CAC's `[chat-foot-start]` slot until
   * the library exposes a first-class `composerContext` input).
   */
  readonly composerContext = input<ComposerLocationContext | null>(null);

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
   * "Where am I" context-header inputs. The host resolves the lane/state
   * and the live run for the scope in view (the open task, or — on the
   * board — the running task in the active project) and hands them here so
   * the context header can render the operator's current location without
   * re-fetching. `activeJobKey` is the short display key (e.g. `AGT-1916`);
   * `activeJobState` is the canonical lane key; `activeRun` is non-null only
   * while a CLI run is executing in scope.
   */
  readonly activeJobKey = input<string | null>(null);
  readonly activeJobState = input<string | null>(null);
  readonly activeRun = input<{ model: string | null; startedAt: string | null } | null>(null);

  /**
   * Phase: Verbose Debug. Emitted when the user clicks the bug icon in the
   * sheet header while a task tab is in scope. The host (app shell) opens
   * the read-only Verbose Debug overlay against the active task by fetching
   * its evidence (cli output, run timeline, screenshots) and feeding the
   * shared `<app-verbose-debug-overlay>` component.
   */
  readonly openVerboseDebug = output<{ jobId: string; watchPath: string; jobTitle: string | null }>();

  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();

  /**
   * Emitted when the user clicks the settings (⚙) button in the sidesheet
   * header. The host opens the Orchestrator Settings modal (which uses the
   * project-shell rail + panel layout). The modal replaces the former
   * inline "Logic" tab so the sidesheet stays Chat-centric.
   */
  readonly openSettings = output<void>();
  readonly navigateToContext = output<string>();

  readonly open = signal(false);

  /** Persisted resize state for the panel — see OrchestratorPanelStateService. */
  private readonly panelState = inject(OrchestratorPanelStateService);
  readonly panelWidth = this.panelState.width;
  readonly activeProject = signal<string | null>(null);
  readonly selectedContextKey = signal<string | null>(null);
  readonly contextSessions = signal<OrchestratorContextSession[]>([]);
  readonly contextMenuOpen = signal(false);
  readonly contextDigestState = inject(OrchestratorContextDigestService);
  private readonly seenContexts = signal<Record<string, string>>(this.readSeenContexts());

  /** Total selectable scopes represented by the header context badge. */
  readonly contextCount = computed(() => {
    const keys = new Set<string>(['global']);
    for (const project of this.projects()) keys.add(`project:${project}`);
    for (const session of this.contextSessions()) keys.add(session.contextKey);
    return keys.size;
  });

  readonly unreadContextKeys = computed<ReadonlySet<string>>(() => {
    const seen = this.seenContexts();
    return new Set(this.contextSessions()
      .filter(session => session.updatedAt && session.contextKey !== this.contextKey()
        && session.updatedAt > (seen[session.contextKey] ?? ''))
      .map(session => session.contextKey));
  });

  private readonly selectedSession = computed(() => {
    const key = this.selectedContextKey();
    return key ? this.contextSessions().find(session => session.contextKey === key) ?? null : null;
  });

  private readonly selectedTask = computed(() => {
    const session = this.selectedSession();
    if (!session || session.kind !== 'task') return null;
    return this.jobService.jobs().find(job => job.projectName === session.projectId
      && (job.taskKey === session.taskKey || job.displayKey === session.taskKey || job.key === session.taskKey)) ?? null;
  });

  /**
   * MC-2 (Concept §4): the side sheet's context follows the operator's
   * navigation — a task page yields a `task` context, the board yields a
   * `project` context. `pinned` freezes that so navigating away no longer
   * auto-switches the sheet; the frozen scope is captured in
   * {@link pinnedSnapshot} at pin time. There is deliberately no "create
   * context" UI: the context is derived from navigation, never authored.
   */
  readonly pinned = signal(false);
  private readonly pinnedSnapshot = signal<{
    project: string | null;
    jobId: string | null;
    jobTitle: string | null;
    jobKey: string | null;
    jobState: string | null;
    watchPath: string | null;
  } | null>(null);

  readonly effectiveProject = computed<string | null>(() =>
    this.selectedContextKey() === 'global'
      ? null
      : this.selectedSession()
      ? this.selectedSession()?.projectId ?? null
      : (this.pinned() ? (this.pinnedSnapshot()?.project ?? null) : this.activeProject()));
  readonly effectiveJobId = computed<string | null>(() =>
    this.selectedContextKey() === 'global'
      ? null
      : this.selectedSession()?.kind === 'task'
      ? (this.selectedTask()?.id ?? null)
      : (this.selectedSession() ? null : (this.pinned() ? (this.pinnedSnapshot()?.jobId ?? null) : this.activeJobId())));
  readonly effectiveJobTitle = computed<string | null>(() =>
    this.selectedContextKey() === 'global'
      ? null
      : this.selectedSession()?.kind === 'task'
      ? (this.selectedTask()?.title ?? this.selectedSession()?.taskKey ?? null)
      : (this.selectedSession() ? null : (this.pinned() ? (this.pinnedSnapshot()?.jobTitle ?? null) : this.activeJobTitle())));
  readonly effectiveJobKey = computed<string | null>(() =>
    this.selectedContextKey() === 'global'
      ? null
      : this.selectedSession()?.kind === 'task'
      ? (this.selectedSession()?.taskKey ?? null)
      : (this.selectedSession() ? null : (this.pinned() ? (this.pinnedSnapshot()?.jobKey ?? null) : this.activeJobKey())));
  readonly effectiveJobState = computed<string | null>(() =>
    this.selectedContextKey() === 'global'
      ? null
      : this.selectedSession()?.kind === 'task'
      ? (this.selectedTask()?.state ?? null)
      : (this.selectedSession() ? null : (this.pinned() ? (this.pinnedSnapshot()?.jobState ?? null) : this.activeJobState())));
  readonly effectiveWatchPath = computed<string | null>(() =>
    this.selectedContextKey() === 'global'
      ? null
      : this.selectedSession()?.kind === 'task'
      ? (this.selectedTask()?.watchPath ?? null)
      : (this.selectedSession() ? null : (this.pinned() ? (this.pinnedSnapshot()?.watchPath ?? null) : this.activeWatchPath())));

  /**
   * Navigation-derived context kind and canonical context key. A task
   * context needs both an id and a title in scope; anything else is the
   * project (board) context. The key mirrors the backend registry shape
   * (`project:<PROJ>` / `task:<PROJ>/<KEY>`, see OrchestratorContextKey) and
   * the chat body reads and writes through it (see {@link readChat} and the
   * context-aware send in {@link onSubmit}), so a task page and the board no
   * longer share one history.
   */
  readonly contextKind = computed<'task' | 'project'>(() =>
    this.effectiveJobId() && this.effectiveJobTitle() ? 'task' : 'project');
  readonly contextKey = computed<string | null>(() => {
    if (this.selectedContextKey()) return this.selectedContextKey();
    const proj = (this.effectiveProject() ?? '').trim();
    if (!proj) return null;
    if (this.contextKind() === 'task') {
      const key = (this.effectiveJobKey() ?? '').trim();
      if (key) return `task:${proj}/${key}`;
    }
    return `project:${proj}`;
  });

  readonly turns = signal<OrchestratorChatTurn[]>([], { equal: sameOrchestratorChatTurns });
  readonly loading = signal(false);
  readonly sending = signal(false);
  readonly errorMsg = signal<string | null>(null);
  readonly executionContext = signal<ChatExecutionContext | null>(null);
  readonly executionHostLabel = computed(() => {
    const context = this.executionContext();
    if (!context) return 'Execution context unavailable';
    return context.executionKind === 'local' ? 'Local' : context.hostName;
  });
  readonly executionRefLabel = computed(() => {
    const context = this.executionContext();
    if (!context) return '';
    if (context.state !== 'ready' || !context.repoPath)
      return `Resolving ${context.branch ?? 'project'} checkout`;
    const head = context.headSha ? context.headSha.slice(0, 8) : 'unknown';
    return `${context.repoPath} · ${context.branch ?? 'detached'}@${head}`;
  });
  readonly executionContextTitle = computed(() => {
    const context = this.executionContext();
    if (!context) return 'Execution context unavailable';
    return [
      `Execution: ${this.executionHostLabel()}`,
      `Repository: ${context.repoPath ?? 'resolving'}`,
      `Branch: ${context.branch ?? 'unknown'}`,
      `HEAD: ${context.headSha ?? 'unknown'}`,
    ].join('\n');
  });

  /**
   * F14 navigation-context send caching. The menu toggle dedupes sends
   * that would ship the same `navigationContext`: first send on a
   * (project, task) pair carries the full block, identical subsequent
   * sends carry `null`. Dismiss forces the next send to `null` even on
   * a context change; switching project or task re-arms context inclusion.
   */
  readonly contextDismissed = signal(false);
  private readonly lastSentContextSignature = signal<string | null>(null);
  private lastSentProjectForSignature: string | null = null;

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

  /**
   * Composer toolbar items. The chat component is intentionally generic
   * (a reusable agent-interaction surface) — hosts plug surface-specific
   * affordances in. The orchestrator side sheet retains the standard
   * reference, mention, fork, and search actions.
   */
  readonly composerToolbarStart: readonly ChatToolbarItem[] = [
    { id: 'reference', glyph: '#', label: 'Reference a task' },
    { id: 'mention',   glyph: '@', label: 'Mention a participant' },
    { id: 'fork',      glyph: '⑂', label: 'Fork into a new thread' },
    { id: 'search',    glyph: '🔍', label: 'Search chat history' },
  ];
  /** Effective GPT-only route and explicit-vs-inherited provenance. */
  readonly composerRoutingLabel = computed<string>(() =>
    `GPT-only · ${this.composerModel.sourceLabel()}`);

  onModelCommit(selection: ChatModelSelection): void {
    this.composerModel.commit(selection);
  }

  private pollTimer: VisibleIntervalHandle | null = null;

  /** Canonical next-gen transcript consumed by `<cac-conversation-view>`. */
  readonly conversationEvents = computed(() => buildOrchestratorConversationEvents(
    this.turns(),
    this.localTurns(),
    this.events(),
    this.effectiveProject(),
    this.contextKey() ?? this.effectiveProject() ?? 'orchestrator-chat',
  ));

  readonly contextChipText = computed<string | null>(() => {
    const proj = this.effectiveProject();
    if (!proj) return null;
    const tail = this.contextKind() === 'task'
      ? `Task '${this.effectiveJobTitle()}'`
      : 'Board';
    return `Context: ${proj} · ${tail}`;
  });

  constructor() {
    // MC-2: reload when the *effective context* changes — pinning freezes
    // the scope, so following the effective value (not the raw picker)
    // keeps a pinned sheet on its frozen thread while nav moves on. We track
    // contextKey() (not just the project) so navigating between the board and
    // a task in the same project — a project↔task context switch that leaves
    // effectiveProject() unchanged — still swaps the visible transcript.
    effect(() => {
      const proj = this.effectiveProject();
      const key = this.contextKey();
      untracked(() => this.contextDigestState.selectContext(key));
      this.open();
      if (this.open() && key) {
        this.localTurns.set([]);
        untracked(() => this.contextDigestState.load(key, false));
        if (proj) this.refresh(false);
      }
    });

    // F14: any picker move (project or task) re-arms context inclusion
    // and (on project change) clears the cache so the new thread sees
    // a fresh context block on its first send.
    effect(() => {
      const proj = this.effectiveProject();
      this.effectiveJobId();
      untracked(() => {
        if (this.contextDismissed()) this.contextDismissed.set(false);
        if (proj !== this.lastSentProjectForSignature) {
          this.lastSentContextSignature.set(null);
          this.lastSentProjectForSignature = proj;
        }
      });
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
        // MC-2: a pinned sheet ignores navigation — do not let the host's
        // preferred project snap the frozen scope back.
        if (this.pinned()) return;
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

  }

  /**
   * MC-2: pin freezes the current navigation context; unpin resumes
   * following navigation. Pinning snapshots the live scope so the badge,
   * context menu, and chat body all stay on it. Unpinning snaps the project back to
   * the host's current preferred project because the preferred-sync effect
   * only fires on an input change — without this nudge an unpinned sheet
   * would keep the frozen project until the operator next navigated.
   */
  togglePin(): void {
    if (this.pinned()) {
      this.pinned.set(false);
      this.pinnedSnapshot.set(null);
      const preferred = this.preferredProject();
      if (preferred && this.projects().includes(preferred)) {
        this.activeProject.set(preferred);
      }
      return;
    }
    this.pinnedSnapshot.set({
      project: this.activeProject(),
      jobId: this.activeJobId(),
      jobTitle: this.activeJobTitle(),
      jobKey: this.activeJobKey(),
      jobState: this.activeJobState(),
      watchPath: this.activeWatchPath(),
    });
    this.pinned.set(true);
  }

  ngOnInit(): void {
    // Slow poll: the chat history only changes when the user sends a
    // message (which we already refresh after) or when something else
    // appends a turn. 30s keeps the UI honest without burning quota.
    this.pollTimer = setVisibleInterval(() => {
      if (this.open() && this.effectiveProject() && !this.loading() && !this.sending()) {
        this.refresh(true);
      }
    }, 30_000);

    this.maybeSeedDemoEvents();
    this.refreshContextSessions();
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
    this.events.set(buildDemoEvents(Date.now()));
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearVisibleInterval(this.pollTimer);
    this.pollTimer = null;
  }

  show(): void { this.open.set(true); }
  hide(): void {
    this.contextMenuOpen.set(false);
    this.open.set(false);
  }
  toggleContextMenu(): void {
    this.contextMenuOpen.update(open => !open);
  }
  toggle(): void {
    if (this.open()) {
      this.hide();
    } else {
      this.show();
    }
  }

  setActiveProject(proj: string): void {
    // MC-2: the picker is inert while pinned — the frozen scope wins until
    // the operator unpins.
    if (this.pinned()) return;
    this.selectedContextKey.set(null);
    if (proj === this.activeProject()) return;
    this.activeProject.set(proj);
  }

  selectChatContext(contextKey: string): void {
    this.selectedContextKey.set(contextKey);
    const session = this.contextSessions().find(item => item.contextKey === contextKey);
    const projectId = session?.projectId
      ?? (contextKey.startsWith('project:') ? contextKey.slice('project:'.length) : null);
    if (projectId) this.activeProject.set(projectId);
    const updatedAt = session?.updatedAt ?? new Date().toISOString();
    this.seenContexts.update(seen => ({ ...seen, [contextKey]: updatedAt }));
    this.persistSeenContexts();
    this.contextMenuOpen.set(false);
  }

  onNavigateToContext(contextKey: string): void {
    this.selectedContextKey.set(null);
    this.contextMenuOpen.set(false);
    this.navigateToContext.emit(contextKey);
  }
  private refreshContextSessions(): void {
    this.jobService.getOrchestratorContextSessions().subscribe({
      next: response => this.contextSessions.set(response.sessions ?? []),
      error: () => this.contextSessions.set([]),
    });
  }

  refreshCurrentContext(): void {
    const key = this.contextKey();
    if (!key || this.contextDigestState.refreshing()) return;
    this.contextDigestState.load(key, true);
    if (this.effectiveProject()) this.refresh(false);
    this.refreshContextSessions();
  }

  private readSeenContexts(): Record<string, string> {
    if (typeof window === 'undefined') return {};
    try { return JSON.parse(window.localStorage?.getItem('atp.chatSwitcher.seen.v1') ?? '{}'); }
    catch { return {}; }
  }
  private persistSeenContexts(): void {
    if (typeof window === 'undefined') return;
    try { window.localStorage?.setItem('atp.chatSwitcher.seen.v1', JSON.stringify(this.seenContexts())); }
    catch { /* optional local read state */ }
  }

  selectProjectTab(proj: string): void {
    this.setActiveProject(proj);
  }

  /**
   * Drag the left-edge splitter to resize the panel. The orchestrator
   * sits on the right of the viewport (flex-direction: row-reverse on
   * .app-shell), so dragging the handle LEFT widens the panel: dx =
   * startX - clientX, newWidth = startW + dx. Width is committed to
   * OrchestratorPanelStateService which persists to localStorage and
   * clamps within [360, min(1100, 96vw)].
   */
  startResize(event: MouseEvent): void {
    event.preventDefault();
    const startX = event.clientX;
    const startW = this.panelWidth();
    const onMove = (e: MouseEvent) => {
      this.panelState.setWidth(startW + (startX - e.clientX));
    };
    const onUp = () => {
      window.removeEventListener('mousemove', onMove);
      window.removeEventListener('mouseup', onUp);
      document.body.style.cursor = '';
      document.body.style.userSelect = '';
    };
    document.body.style.cursor = 'ew-resize';
    document.body.style.userSelect = 'none';
    window.addEventListener('mousemove', onMove);
    window.addEventListener('mouseup', onUp);
  }


  onOpenSettings(): void {
    this.openSettings.emit();
  }

  toggleNextMessageContext(): void {
    this.contextDismissed.update(dismissed => !dismissed);
  }

  private currentContextSignature(): string {
    const proj = (this.effectiveProject() ?? '').trim();
    const jobId = (this.effectiveJobId() ?? '').trim();
    const jobTitle = (this.effectiveJobTitle() ?? '').trim();
    return `${proj}|${jobId}|${jobTitle}`;
  }

  onOpenVerboseDebug(): void {
    const jobId = this.effectiveJobId();
    const watchPath = this.effectiveWatchPath();
    if (!jobId || !watchPath) return;
    this.openVerboseDebug.emit({ jobId, watchPath, jobTitle: this.effectiveJobTitle() });
  }

  /**
   * MC-2 (Concept §4): read the transcript for the *current navigation
   * context*, not just the project. On a task page the sheet reads the
   * `task:<PROJ>/<KEY>` thread; on the board it reads `project:<PROJ>`, which
   * the backend resolves to the same canonical per-project log the plain
   * project route serves. Falling back to the project read when no context
   * key is derivable keeps the board's behaviour byte-for-byte unchanged.
   */
  private readChat(proj: string) {
    const key = this.contextKey();
    return key
      ? this.jobService.getOrchestratorChatByContext(key)
      : this.jobService.getOrchestratorChat(proj);
  }

  refresh(silent = false): void {
    const proj = this.effectiveProject();
    if (!proj) return;
    if (!silent) this.loading.set(true);
    this.readChat(proj).subscribe({
      next: (resp) => {
        this.turns.set(resp.turns ?? []);
        this.executionContext.set(resp.executionContext ?? null);
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

  /**
   * Slice E: lookup table from inline event card id to the job it was
   * filed for. Populated when a `/bug ...` directive succeeds; consumed
   * by {@link onChatEventAction} to open the detail panel of that job
   * without parsing the rendered markdown back out.
   */
  private readonly bugEventTargets = new Map<string, { jobId: string; watchPath: string }>();

  async onSubmit(event: ChatSubmitEvent): Promise<void> {
    const proj = this.effectiveProject();
    if (!proj) return;
    const text = event.text.trim();
    if (!text && event.attachments.length === 0) return;

    // Slice E: chat-level slash directive. The parser lives here in the
    // chat host (not a global registry) because the directive borrows
    // the chat's X-Client-Id (via the HttpClient interceptor) and the
    // active-project watch-path lookup, and routes its outcome into the
    // existing `events` stream so the confirmation card appears in the
    // chat at the user's turn position.
    if (text === '/bug' || text.startsWith('/bug ') || text.startsWith('/bug\n')) {
      this.handleBugDirective(text, event, proj);
      return;
    }

    // Render the user's turn immediately so the chat doesn't sit silent
    // while the orchestrator thinks (Opus replies often take 30-60s).
    // The attached image is carried on the local turn as a `blob:` URL
    // so the bubble paints with text + image in the same frame; without
    // this the bubble would appear with text only and the image would
    // pop in moments later once the server turn arrived.
    const localBlobs = event.attachments.map((a) => ({
      alt: a.alt,
      previewUrl: a.previewUrl
    }));
    const localId = `local:${Date.now()}`;
    const localTurn: OrchestratorChatTurn & {
      pending?: boolean;
      localAttachments?: { alt: string; previewUrl: string }[];
    } = {
      id: localId,
      ts: new Date().toISOString(),
      role: 'user',
      text: text || (event.attachments.length > 0 ? '(attachments)' : ''),
      pending: true,
      localAttachments: localBlobs.length > 0 ? localBlobs : undefined
    };
    this.localTurns.update((curr) => [...curr, localTurn]);
    this.sending.set(true);

    // Upload each pasted/dropped image first so the chat message can
    // reference real files. We do this sequentially to keep error
    // surfaces simple and the frontend code small; orchestrator chats
    // rarely carry more than 1-2 images per turn. We also read each file
    // as base64 in parallel so the same POST can carry the bytes inline:
    // the backend uses the inline bytes to build an Anthropic image
    // content block (model sees the picture without a Read tool call),
    // while the uploaded copy stays as the archived reference.
    const uploaded: {
      alt: string;
      relativePath: string;
      inlineBase64?: string | null;
      mimeType?: string | null;
    }[] = [];
    try {
      for (const att of event.attachments) {
        const [resp, inline] = await Promise.all([
          this.uploadOne(proj, att.file),
          readFileAsBase64(att.file).catch(() => null)
        ]);
        uploaded.push({
          alt: att.alt,
          relativePath: resp.relativePath,
          inlineBase64: inline?.base64 ?? null,
          mimeType: inline?.mimeType ?? att.file.type ?? null
        });
      }
    } catch (err) {
      this.sending.set(false);
      const message = (err as { message?: string })?.message ?? 'Attachment upload failed';
      this.localTurns.update((curr) =>
        curr.map((t) => (t.id === localId ? { ...t, pending: false, errorMessage: message } : t))
      );
      return;
    }

    // F14: ship full nav context only on the first send per (project,
    // task) pair; identical sends and dismissed sends ship `null`.
    const contextSignature = this.currentContextSignature();
    const shouldShipContext =
      !this.contextDismissed() && contextSignature !== this.lastSentContextSignature();
    const contextPayload = shouldShipContext
      ? buildChatNavigationContext({
          activeJobId: this.effectiveJobId(),
          activeJobTitle: this.effectiveJobTitle()
        })
      : null;

    // MC-2: route the send to the current context thread so a task page's
    // turns accumulate in — and read back from — their own history. A
    // project/board context falls through to the per-project route.
    const contextKey = this.contextKey();
    const sendBody = {
      text: text || (uploaded.length > 0 ? '(attachments)' : ''),
      attachments: uploaded.length > 0 ? uploaded : undefined,
      navigationContext: contextPayload,
      model: this.composerModel.effectiveSelection().model || null,
      thinkingLevel: this.composerModel.effectiveSelection().thinkingLevel,
      selectionSource: this.composerModel.selectionSource(),
    };
    const send$ = contextKey
      ? this.jobService.sendOrchestratorChatByContext(contextKey, sendBody)
      : this.jobService.sendOrchestratorChat(proj, sendBody);
    send$.subscribe({
      next: (response) => {
        if (response.executionContext) this.executionContext.set(response.executionContext);
        if (shouldShipContext) {
          this.lastSentContextSignature.set(contextSignature);
        }
        this.sending.set(false);
        // Pre-decode the persisted attachment URL(s) so the upcoming swap
        // from the local blob bubble to the server turn uses byte-identical
        // pixels from the browser image cache (no fetch on swap = no
        // visible flicker). The fallback timeout caps the wait so a slow
        // network can't strand the bubble in pending state forever.
        const preloads = uploaded.map((u) =>
          new Promise<void>((resolve) => {
            const img = new Image();
            img.onload = () => resolve();
            img.onerror = () => resolve();
            img.src = resolveAttachmentUrl(proj, u.relativePath);
          })
        );
        // Fetch the server's view of the conversation. While the local
        // turn is still in the list, `suppressLocalDuplicates` hides the
        // matching server user turn so the bubble does not duplicate
        // momentarily. Once preloads resolve we drop the local turn and
        // revoke its blob URLs; the server turn takes over with the
        // cached image and the user perceives no swap.
        this.readChat(proj).subscribe({
          next: async (resp) => {
            this.turns.set(resp.turns ?? []);
            this.errorMsg.set(null);
            if (preloads.length > 0) {
              await Promise.race([
                Promise.all(preloads),
                new Promise<void>((r) => setTimeout(r, 3000))
              ]);
            }
            this.localTurns.set([]);
            for (const att of event.attachments) URL.revokeObjectURL(att.previewUrl);
          },
          error: () => {
            // Fallback: clear local turn anyway so the user is not stuck
            // looking at a pending bubble forever.
            this.localTurns.set([]);
            for (const att of event.attachments) URL.revokeObjectURL(att.previewUrl);
          }
        });
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

  /**
   * Slice E: parse `/bug <description>` and create a backlog task via
   * the existing `POST /api/tasks` endpoint. The directive must land in
   * `0-backlog` with `taskType=bug` so it goes through triage instead of
   * skipping straight into `2-ready`. Hashtag patterns at the start of
   * any line in the description are parsed into workspace tag ids.
   */
  private handleBugDirective(text: string, event: ChatSubmitEvent, project: string): void {
    const description = text.replace(/^\/bug\s*/, '').trim();
    const ts = new Date().toISOString();

    // Always render the user's directive locally so the card appears at
    // the user's turn position even though we never round-trip through
    // the orchestrator chat write-path.
    const localId = `bug-local:${Date.now()}`;
    this.localTurns.update((curr) => [
      ...curr,
      { id: localId, ts, role: 'user', text }
    ]);
    for (const att of event.attachments) URL.revokeObjectURL(att.previewUrl);

    if (!description) {
      this.appendBugEvent({
        id: `bug-err:empty:${Date.now()}`,
        kind: 'task',
        timestamp: new Date().toISOString(),
        severity: 'error',
        summary: 'Bug not filed: description is empty',
        detail: 'Add a description after `/bug`, e.g. `/bug Frontend chips overlap on narrow viewport`.'
      });
      return;
    }

    const watchPath = this.watchPaths().find((wp) => wp.name === project)?.path;
    if (!watchPath) {
      this.appendBugEvent({
        id: `bug-err:no-watchpath:${Date.now()}`,
        kind: 'task',
        timestamp: new Date().toISOString(),
        severity: 'error',
        summary: 'Bug not filed: no watch path for this project',
        detail: `Could not resolve a watch path for project \`${project}\`. Check the workspace configuration.`
      });
      return;
    }

    const tags = parseBugHashtags(description);
    const firstLine = description.split('\n')[0].trim();
    const title = firstLine.length > 80 ? firstLine.slice(0, 77) + '...' : firstLine;
    const promptMarkdown = `${description}\n\n---\n\nReported via /bug from project chat`;

    this.jobService
      .createJob({
        title,
        agent: 'claude',
        watchPath,
        promptMarkdown,
        targetState: TaskState.Backlog,
        taskType: 'bug',
        tags: tags.length > 0 ? tags : undefined
      })
      .subscribe({
        next: (resp) => {
          const jobId = resp.id;
          const eventId = `bug-ok:${jobId}`;
          this.bugEventTargets.set(eventId, { jobId, watchPath });
          const tagSuffix = tags.length > 0 ? `\n\nTags: ${tags.map((t) => '`' + t + '`').join(' ')}` : '';
          this.appendBugEvent({
            id: eventId,
            kind: 'task',
            timestamp: new Date().toISOString(),
            summary: `Bug filed in 0-backlog: ${title}`,
            detail:
              `**Lane:** \`0-backlog\`  \n` +
              `**Task type:** \`bug\`  \n` +
              `**Job ID:** \`${jobId}\`${tagSuffix}\n\n` +
              `The new task is in triage. Open the detail panel to refine the prompt before promoting it to \`2-ready\`.`,
            actionLabel: 'Open task'
          });
          // Refresh the kanban so the new card surfaces in the backlog
          // lane without waiting for the next poll tick.
          this.jobService.refresh(true);
        },
        error: (err) => {
          const message =
            err?.error?.error || (typeof err?.error === 'string' ? err.error : null) || err?.message || 'Failed to file bug';
          this.appendBugEvent({
            id: `bug-err:${Date.now()}`,
            kind: 'task',
            timestamp: new Date().toISOString(),
            severity: 'error',
            summary: `Bug not filed: ${title || '(empty title)'}`,
            detail: `**Error:** ${message}`
          });
        }
      });
  }

  private appendBugEvent(ev: ChatEvent): void {
    this.events.update((curr) => [...curr, ev]);
  }

  /**
   * Slice E: route an inline event-card action click. Currently the
   * only consumer is the bug-confirmation card's "Open task" button,
   * which opens the kanban detail panel for the newly-filed job in the
   * same tab via the host's existing `openDetail` flow.
   */
  onChatEventAction(action: { eventId: string }): void {
    const target = this.bugEventTargets.get(action.eventId);
    if (!target) return;
    this.openJobDetail.emit(target);
  }

  hasChatEventAction(eventId: string): boolean {
    return this.bugEventTargets.has(eventId);
  }

  private uploadOne(projectName: string, file: File): Promise<{ relativePath: string; url: string }> {
    return new Promise((resolve, reject) => {
      this.jobService.uploadOrchestratorChatAttachment(projectName, file).subscribe({
        next: (resp) => resolve({ relativePath: resp.relativePath, url: resp.url }),
        error: (err) => reject(new Error(err?.error?.error || err?.message || 'Upload failed'))
      });
    });
  }

}
