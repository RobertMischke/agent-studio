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
import { TaskService } from '../../../../services/task.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { WatchPathEntry } from '../../../../models/task.model';
import type {
  ChatExecutionContext,
  ComposerLocationContext,
  OrchestratorChatTurn,
  OrchestratorContextSession,
  OrchestratorContextSourceOption,
} from '../../../../features/orchestrator';
import {
  buildChatNavigationContext,
} from '../../../../features/orchestrator';
import { ChatComponent } from 'coding-agent-chat/composer';
import { ConversationViewComponent } from 'coding-agent-chat/conversation';
import {
  ChatEvent,
  ChatComposerContext,
  ChatModelSelection,
  ChatSubmitEvent,
  ChatToolbarItem,
} from 'coding-agent-chat/core';
import { SidesheetComponent } from '../../../../components/sidesheet/sidesheet.component';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import { OrchestratorContextHeaderComponent } from '../orchestrator-context-header/orchestrator-context-header.component';
import { ChatSwitcherRailComponent } from '../chat-switcher-rail/chat-switcher-rail.component';
import { OrchestratorProjectPickerComponent } from '../orchestrator-project-picker/orchestrator-project-picker.component';
import { OrchestratorContextReceiptComponent } from '../orchestrator-context-receipt/orchestrator-context-receipt.component';
import { OrchestratorContextPickerComponent } from '../orchestrator-context-picker/orchestrator-context-picker.component';
import { OrchestratorPanelStateService } from '../../state/orchestrator-panel-state.service';
import { OrchestratorContextDigestService } from '../../state/orchestrator-context-digest.service';
import { OrchestratorComposerModelService } from '../../state/orchestrator-composer-model.service';
import {
  buildOrchestratorConversationEvents,
  sameOrchestratorChatTurns,
} from './orchestrator-side-sheet.util';
import {
  buildNavigationContextKey,
  orchestratorContextErrorMessage,
  parseOrchestratorContextKey,
  resolveEffectiveContextKey,
} from './orchestrator-context-key.util';
import { pageContextKey, type PageContext } from '../../../../models/page-context.model';
import { UiPreferencesService } from '../../../shell/state/ui-preferences.service';
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
    OrchestratorContextReceiptComponent,
    OrchestratorContextPickerComponent,
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
  /** Prevent a persisted tab from opening Chat before a copied route resolves. */
  readonly projectEntryReady = input(true);
  readonly watchPaths = input<WatchPathEntry[]>([]);
  /**
   * Canonical active-tab context, derived by Studio and rendered through
   * CAC's `composerContext` input.
   */
  readonly composerContext = input<ComposerLocationContext | null>(null);
  /** Active repository page carried inside the existing project chat. */
  readonly pageContext = input<PageContext | null>(null);

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
  private readonly uiPreferences = inject(UiPreferencesService);
  readonly panelWidth = this.panelState.width;
  readonly activeProject = signal<string | null>(null);
  readonly selectedContextKey = signal<string | null>(null);
  private readonly selectedContextNavigationKey = signal<string | null>(null);
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
    const key = this.effectiveSelectionKey();
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
    dossierKey: string | null;
    dossierId: string | null;
    dossierTitle: string | null;
    page: PageContext | null;
  } | null>(null);

  /**
   * Synchronous active-tab task projection shared with the visible composer
   * footer. It wins over the asynchronously hydrated detail inputs so the
   * request cannot briefly fall back to board scope during a tab switch.
   */
  private readonly composerTaskContext = computed(() => {
    if (this.pageContext()) return null;
    const context = this.composerContext();
    return context?.taskKey ? context : null;
  });

  private readonly navigationProject = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.project ?? null)
      : (this.pageContext()?.projectName ?? this.composerTaskContext()?.project ?? this.activeProject()));
  private readonly navigationPage = computed(() =>
    this.pinned() ? (this.pinnedSnapshot()?.page ?? null) : this.pageContext());
  private readonly navigationJobId = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.jobId ?? null)
      : (this.composerTaskContext()?.taskId ?? this.activeJobId()));
  private readonly navigationJobTitle = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.jobTitle ?? null)
      : (this.composerTaskContext()?.taskTitle ?? this.activeJobTitle()));
  private readonly navigationJobKey = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.jobKey ?? null)
      : (this.composerTaskContext()?.taskKey ?? this.activeJobKey()));
  private readonly navigationJobState = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.jobState ?? null)
      : (this.composerTaskContext()?.taskState ?? this.activeJobState()));
  private readonly navigationWatchPath = computed(() =>
    this.pinned()
      ? (this.pinnedSnapshot()?.watchPath ?? null)
      : (this.composerTaskContext()?.taskWatchPath ?? this.activeWatchPath()));
  private readonly navigationDossierKey = computed(() => this.pinned()
    ? (this.pinnedSnapshot()?.dossierKey ?? null)
    : (this.navigationPage()?.dossierKey ?? this.composerContext()?.dossierKey ?? null));
  private readonly navigationDossierRouteActive = computed(() => this.pinned()
    ? !!this.pinnedSnapshot()?.dossierId
    : this.navigationPage()?.pageType === 'workbench' || !!this.composerContext()?.dossierId);
  private readonly navigationContextKey = computed(() => buildNavigationContextKey(
    this.navigationProject(),
    this.navigationJobKey(),
    this.navigationDossierKey(),
    this.navigationDossierRouteActive(),
  ));
  private readonly contextResolution = computed(() => resolveEffectiveContextKey(
    this.navigationContextKey(),
    this.selectedContextKey(),
    this.selectedContextNavigationKey(),
    this.projects(),
    this.contextSessions(),
  ));
  private readonly effectiveSelectionKey = computed(() =>
    this.contextResolution().discardedSelection ? null : this.selectedContextKey());
  private readonly parsedContext = computed(() => parseOrchestratorContextKey(this.contextResolution().key));

  readonly effectiveProject = computed<string | null>(() =>
    this.parsedContext()?.projectId ?? null);
  readonly effectiveJobId = computed<string | null>(() =>
    this.parsedContext()?.kind !== 'task'
      ? null
      : this.effectiveSelectionKey()
        ? (this.selectedTask()?.id ?? null)
        : this.navigationJobId());
  readonly effectiveJobTitle = computed<string | null>(() =>
    this.parsedContext()?.kind !== 'task'
      ? null
      : this.effectiveSelectionKey()
        ? (this.selectedTask()?.title ?? this.parsedContext()?.taskKey ?? null)
        : (this.navigationJobTitle() ?? this.parsedContext()?.taskKey ?? null));
  readonly effectiveJobKey = computed<string | null>(() =>
    this.parsedContext()?.kind === 'task' ? (this.parsedContext()?.taskKey ?? null) : null);
  readonly effectiveDossierKey = computed<string | null>(() =>
    this.parsedContext()?.kind === 'dossier' ? (this.parsedContext()?.dossierKey ?? null) : null);
  private readonly effectivePage = computed<PageContext | null>(() => {
    const page = this.navigationPage();
    const parsed = this.parsedContext();
    if (!page || !parsed) return null;
    if (parsed.kind === 'dossier') {
      return page.pageType === 'workbench' && page.dossierKey === parsed.dossierKey ? page : null;
    }
    return parsed.kind === 'project' && page.pageType !== 'workbench' ? page : null;
  });
  readonly effectiveDossierTitle = computed<string | null>(() =>
    this.effectivePage()?.title
    ?? (this.parsedContext()?.kind === 'dossier' ? this.effectiveDossierKey() : null));
  readonly effectiveJobState = computed<string | null>(() =>
    this.parsedContext()?.kind !== 'task'
      ? null
      : this.effectiveSelectionKey()
        ? (this.selectedTask()?.state ?? null)
        : this.navigationJobState());
  readonly effectiveWatchPath = computed<string | null>(() =>
    this.parsedContext()?.kind !== 'task'
      ? null
      : this.effectiveSelectionKey()
        ? (this.selectedTask()?.watchPath ?? null)
        : this.navigationWatchPath());

  /**
   * Navigation-derived context kind and canonical context key. Task and
   * Dossier routes require their stable document key; other project routes
   * resolve to the single project chat. The chat body reads and writes only
   * through this key, so document pages and project surfaces cannot share
   * one history.
   */
  readonly contextKind = computed<'task' | 'dossier' | 'project'>(() => {
    const kind = this.parsedContext()?.kind;
    return kind === 'task' || kind === 'dossier' ? kind : 'project';
  });
  readonly contextKey = computed<string | null>(() => this.contextResolution().key);

  readonly turns = signal<OrchestratorChatTurn[]>([], { equal: sameOrchestratorChatTurns });
  readonly latestContextReceipt = computed(() =>
    [...this.turns()].reverse().find(turn => turn.role === 'orchestrator' && turn.contextReceipt)?.contextReceipt ?? null);
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
  readonly executionRepoLabel = computed(() => {
    const context = this.executionContext();
    if (!context) return '';
    if (context.state !== 'ready' || !context.repoPath) return 'Resolving checkout';
    return context.repoPath;
  });
  readonly executionRevisionLabel = computed(() => {
    const context = this.executionContext();
    if (!context) return '';
    if (context.state !== 'ready' || !context.repoPath) return context.branch ?? 'project';
    const head = context.headSha ? context.headSha.slice(0, 8) : 'unknown';
    return `· ${context.branch ?? 'detached'}@${head}`;
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

  /** Project scope may explicitly omit context once. Task scope is mandatory. */
  readonly contextDismissed = signal(false);
  readonly contextAttachments = signal<OrchestratorContextSourceOption[]>([]);
  private readonly contextPicker = viewChild<OrchestratorContextPickerComponent>('contextPicker');

  readonly automaticContextLabel = computed(() => {
    const project = this.effectiveProject();
    const page = this.effectivePage();
    if (page && page.projectName === project) return `${page.pageType === 'workbench' ? 'Dossier' : 'Page'} · ${page.title}`;
    if (this.contextKind() === 'task') return `Task · ${this.effectiveJobKey() ?? this.effectiveJobTitle() ?? 'current'}`;
    const location = this.composerContext();
    if (location && (!location.project || location.project === project)) {
      return location.detail ? `${location.surface} · ${location.detail}` : location.surface;
    }
    return 'Project overview';
  });

  readonly currentTabSource = computed<OrchestratorContextSourceOption | null>(() => {
    if (this.contextKind() !== 'project') return null;
    const project = this.effectiveProject();
    const page = this.effectivePage();
    if (!project || !page || page.projectName !== project) return null;
    const reference = { kind: 'page' as const, reference: pageContextKey(page), projectId: project };
    return {
      id: `${reference.kind}:${project}:${reference.reference}`,
      category: 'current',
      label: page.title,
      detail: `${page.pageType === 'workbench' ? 'Dossier' : 'Page'} · ${page.relPath}`,
      estimateTokens: 1_200,
      reference,
    };
  });
  readonly cacComposerContext = computed<ChatComposerContext | null>(() => {
    const context = this.composerContext();
    if (!context?.project) return null;
    return { project: context.project, surface: context.surface, detail: context.detail ?? '' };
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
  private readonly eventsContextKey = signal<string | null>(null);

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
  private lastProjectEntry: string | null = null;
  private contextSessionsLoading = false;
  private chatRequestVersion = 0;

  /** Canonical next-gen transcript consumed by `<cac-conversation-view>`. */
  readonly conversationEvents = computed(() => buildOrchestratorConversationEvents(
    this.turns(),
    this.localTurns(),
    this.eventsContextKey() === this.contextKey() ? this.events() : [],
    this.effectiveProject(),
    this.contextKey() ?? this.effectiveProject() ?? 'orchestrator-chat',
  ));

  readonly contextChipText = computed<string | null>(() => {
    const proj = this.effectiveProject();
    if (!proj) return null;
    const page = this.effectivePage();
    const tail = page
      ? `${page.pageType} '${page.title}'`
      : this.contextKind() === 'dossier'
      ? `Dossier '${this.effectiveDossierKey()}'`
      : this.contextKind() === 'task'
      ? `Task '${this.effectiveJobTitle()}'`
      : 'Board';
    return `Context: ${proj} · ${tail}`;
  });

  constructor() {
    // Session rows can outlive the navigation scope that selected them. The
    // resolver falls back synchronously; this effect also clears stale picker
    // state so subsequent interactions remain on the current navigation key.
    effect(() => {
      const discarded = this.contextResolution().discardedSelection;
      untracked(() => {
        if (!discarded) return;
        this.selectedContextKey.set(null);
        this.selectedContextNavigationKey.set(null);
      });
    });

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
      untracked(() => {
        this.chatRequestVersion += 1;
        this.turns.set([]);
        this.localTurns.set([]);
        this.loading.set(false);
        this.executionContext.set(null);
        this.errorMsg.set(null);
      });
      if (this.open() && key) {
        untracked(() => this.contextDigestState.load(key, false));
        if (proj) this.refresh(false);
      }
    });

    // Context exclusion is a one-message project override. Navigating to a
    // different scope clears it, while task scope remains mandatory.
    effect(() => {
      this.contextKey();
      this.effectivePage();
      untracked(() => {
        if (this.contextDismissed()) this.contextDismissed.set(false);
        if (this.contextAttachments().length > 0) this.contextAttachments.set([]);
        this.contextPicker()?.close();
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

    // Project-level navigation is the standard Chat entry. Wait until route
    // hydration has won over persisted tabs, then align the context before the
    // panel becomes visible. Task tabs keep their separate Chat surface.
    effect(() => {
      const ready = this.projectEntryReady();
      const context = this.composerContext();
      const openOnEntry = this.uiPreferences.openProjectChatOnEntry();
      untracked(() => {
        if (!ready) return;
        const project = context?.project && !context.taskKey ? context.project : null;
        if (!project) {
          this.lastProjectEntry = null;
          return;
        }
        if (project === this.lastProjectEntry) return;
        this.lastProjectEntry = project;
        if (!openOnEntry || this.pinned()) return;
        this.setActiveProject(project);
        this.show();
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
      dossierKey: this.pageContext()?.dossierKey ?? this.composerContext()?.dossierKey ?? null,
      dossierId: this.pageContext()?.dossierId ?? this.composerContext()?.dossierId ?? null,
      dossierTitle: this.pageContext()?.title ?? this.composerContext()?.detail ?? null,
      page: this.pageContext(),
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
    void import('./orchestrator-side-sheet.lazy').then(({ buildDemoEvents }) => {
      this.eventsContextKey.set(this.contextKey());
      this.events.set(buildDemoEvents(Date.now()));
    });
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
    this.selectedContextNavigationKey.set(null);
    if (proj === this.activeProject()) return;
    this.activeProject.set(proj);
  }

  selectChatContext(contextKey: string): void {
    this.selectedContextNavigationKey.set(this.navigationContextKey());
    this.selectedContextKey.set(contextKey);
    const session = this.contextSessions().find(item => item.contextKey === contextKey);
    const updatedAt = session?.updatedAt ?? new Date().toISOString();
    this.seenContexts.update(seen => ({ ...seen, [contextKey]: updatedAt }));
    this.persistSeenContexts();
    this.contextMenuOpen.set(false);
  }

  /** Open one Task Server-managed context from the workspace Chat History. */
  openManagedContext(contextKey: string): void {
    this.show();
    if (this.contextSessions().some(context => context.contextKey === contextKey)) {
      this.selectChatContext(contextKey);
      return;
    }
    this.jobService.getOrchestratorContextSessions().subscribe({
      next: response => {
        const sessions = response.sessions ?? [];
        this.contextSessions.set(sessions);
        if (sessions.some(context => context.contextKey === contextKey)) {
          this.selectChatContext(contextKey);
        }
      },
    });
  }

  onNavigateToContext(contextKey: string): void {
    this.selectedContextKey.set(null);
    this.selectedContextNavigationKey.set(null);
    this.contextMenuOpen.set(false);
    this.navigateToContext.emit(contextKey);
  }
  private refreshContextSessions(): void {
    if (this.contextSessionsLoading) return;
    this.contextSessionsLoading = true;
    this.jobService.getOrchestratorContextSessions().subscribe({
      next: response => {
        this.contextSessions.set(response.sessions ?? []);
        this.contextSessionsLoading = false;
      },
      error: () => {
        this.contextSessions.set([]);
        this.contextSessionsLoading = false;
      },
    });
  }

  refreshCurrentContext(): void {
    const key = this.contextKey();
    if (!key || this.contextDigestState.refreshing()) return;
    this.contextDigestState.load(key, true);
    if (this.effectiveProject()) this.refreshChat(false, false);
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
    if (this.contextKind() !== 'project') return;
    this.contextDismissed.update(dismissed => !dismissed);
  }

  setNextMessageContextIncluded(included: boolean): void {
    if (this.contextKind() !== 'project') return;
    this.contextDismissed.set(!included);
  }

  addContextAttachment(source: OrchestratorContextSourceOption): void {
    if (source.reference.projectId !== this.effectiveProject()) return;
    this.contextAttachments.update(current => current.some(item => item.id === source.id)
      ? current
      : [...current, source]);
  }

  removeContextAttachment(id: string): void {
    this.contextAttachments.update(current => current.filter(item => item.id !== id));
  }

  onComposerToolbarAction(action: { id: string }): void {
    if (action.id === 'reference') this.contextPicker()?.show();
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
   * project route serves. Callers only reach this method after the shared
   * resolver has produced a valid canonical key.
   */
  private readChat(key: string) {
    return this.jobService.getOrchestratorChatByContext(key);
  }

  refresh(silent = false): void {
    this.refreshChat(silent, true);
  }

  private refreshChat(silent: boolean, reconcileMissingSession: boolean): void {
    const proj = this.effectiveProject();
    if (!proj) return;
    const key = this.contextKey();
    if (!key) {
      this.loading.set(false);
      this.errorMsg.set('This chat context is unavailable. Return to a project, task, or Dossier, then try again.');
      return;
    }
    const requestVersion = ++this.chatRequestVersion;
    if (!silent) this.loading.set(true);
    this.readChat(key).subscribe({
      next: (resp) => {
        if (this.contextKey() !== key || this.chatRequestVersion !== requestVersion) return;
        this.turns.set(resp.turns ?? []);
        this.executionContext.set(resp.executionContext ?? null);
        this.errorMsg.set(null);
        if (reconcileMissingSession
          && !this.contextSessions().some((session) => session.contextKey === key)) {
          this.refreshContextSessions();
        }
        if (!silent) this.loading.set(false);
      },
      error: (err) => {
        if (this.contextKey() !== key || this.chatRequestVersion !== requestVersion) return;
        const message = orchestratorContextErrorMessage(err, 'Failed to load orchestrator chat');
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
    // Snapshot once so a navigation change during attachment upload cannot
    // split the send and its reconciliation read across two histories.
    const contextKey = this.contextKey();
    if (!contextKey) {
      this.errorMsg.set('This chat context is unavailable. Return to a project, task, or Dossier, then try again.');
      return;
    }
    const capturedAt = new Date();
    const explicitReferenceSnapshot = this.contextAttachments().map(source => ({ ...source.reference }));
    const navigationSnapshot = {
      kind: this.contextKind(),
      jobId: this.effectiveJobId(),
      taskKey: this.effectiveJobKey(),
      jobTitle: this.effectiveJobTitle(),
      jobState: this.effectiveJobState(),
      page: this.effectivePage(),
    };
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
    const lazy = await import('./orchestrator-side-sheet.lazy');

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
          lazy.uploadAttachment(this.jobService, proj, att.file),
          lazy.readFileAsBase64(att.file).catch(() => null)
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

    // Task scope is attached to every turn. Project scope is also attached by
    // default, with one explicit one-message exclusion available in the menu.
    const boundDocumentScope = navigationSnapshot.kind === 'task' || navigationSnapshot.kind === 'dossier';
    const shouldShipContext = boundDocumentScope || !this.contextDismissed();
    const contextPayload = shouldShipContext
      ? buildChatNavigationContext({
          activeJobId: navigationSnapshot.jobId,
          activeTaskKey: navigationSnapshot.taskKey,
          activeJobTitle: navigationSnapshot.jobTitle,
          activeJobState: navigationSnapshot.jobState,
          pageContext: navigationSnapshot.page,
          now: () => capturedAt,
        })
      : null;

    // MC-2: route the send to the current context thread so a task page's
    // turns accumulate in — and read back from — their own history. A
    // project/board context falls through to the per-project route.
    const sendBody = {
      text: text || (uploaded.length > 0 ? '(attachments)' : ''),
      attachments: uploaded.length > 0 ? uploaded : undefined,
      navigationContext: contextPayload,
      contextEnvelope: lazy.buildOrchestratorContextEnvelope(
        contextKey,
        contextPayload,
        explicitReferenceSnapshot,
        () => capturedAt,
      ),
      model: this.composerModel.effectiveSelection().model || null,
      thinkingLevel: this.composerModel.effectiveSelection().thinkingLevel,
      selectionSource: this.composerModel.selectionSource(),
    };
    const send$ = this.jobService.sendOrchestratorChatByContext(contextKey, sendBody);
    send$.subscribe({
      next: (response) => {
        if (this.contextKey() !== contextKey) {
          this.sending.set(false);
          for (const attachment of event.attachments) URL.revokeObjectURL(attachment.previewUrl);
          return;
        }
        if (response.executionContext) this.executionContext.set(response.executionContext);
        if (!boundDocumentScope && this.contextDismissed()) this.contextDismissed.set(false);
        this.contextAttachments.set([]);
        this.sending.set(false);
        // Pre-decode the persisted attachment URL(s) so the upcoming swap
        // from the local blob bubble to the server turn uses byte-identical
        // pixels from the browser image cache (no fetch on swap = no
        // visible flicker). The fallback timeout caps the wait so a slow
        // network can't strand the bubble in pending state forever.
        const preloads = lazy.preloadPersistedAttachments(proj, uploaded);
        // Fetch the server's view of the conversation. While the local
        // turn is still in the list, `suppressLocalDuplicates` hides the
        // matching server user turn so the bubble does not duplicate
        // momentarily. Once preloads resolve we drop the local turn and
        // revoke its blob URLs; the server turn takes over with the
        // cached image and the user perceives no swap.
        this.readChat(contextKey).subscribe({
          next: async (resp) => {
            if (this.contextKey() !== contextKey) {
              for (const attachment of event.attachments) URL.revokeObjectURL(attachment.previewUrl);
              return;
            }
            this.turns.set(resp.turns ?? []);
            this.errorMsg.set(null);
            await preloads;
            if (this.contextKey() !== contextKey) {
              for (const attachment of event.attachments) URL.revokeObjectURL(attachment.previewUrl);
              return;
            }
            this.localTurns.set([]);
            for (const att of event.attachments) URL.revokeObjectURL(att.previewUrl);
          },
          error: () => {
            if (this.contextKey() !== contextKey) {
              for (const attachment of event.attachments) URL.revokeObjectURL(attachment.previewUrl);
              return;
            }
            // Fallback: clear local turn anyway so the user is not stuck
            // looking at a pending bubble forever.
            this.localTurns.set([]);
            for (const att of event.attachments) URL.revokeObjectURL(att.previewUrl);
          }
        });
      },
      error: (err) => {
        this.sending.set(false);
        if (this.contextKey() !== contextKey) {
          for (const attachment of event.attachments) URL.revokeObjectURL(attachment.previewUrl);
          return;
        }
        const message = orchestratorContextErrorMessage(err, 'Failed to send');
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
    void import('./orchestrator-side-sheet.lazy').then(({ handleBugDirective }) => {
      handleBugDirective({
        text,
        event,
        project,
        watchPaths: this.watchPaths(),
        jobService: this.jobService,
        appendUser: (id, ts, body) => this.localTurns.update(current => [
          ...current, { id, ts, role: 'user', text: body }
        ]),
        appendEvent: item => this.appendBugEvent(item),
        addTarget: (eventId, jobId, watchPath) =>
          this.bugEventTargets.set(eventId, { jobId, watchPath }),
      });
    });
  }

  private appendBugEvent(ev: ChatEvent): void {
    this.eventsContextKey.set(this.contextKey());
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

}
