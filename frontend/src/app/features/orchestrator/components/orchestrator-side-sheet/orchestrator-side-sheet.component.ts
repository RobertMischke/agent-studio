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
import { SidesheetComponent } from '../../../../components/sidesheet/sidesheet.component';
import type { WatchPathEntry } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { clearVisibleInterval, setVisibleInterval, type VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';
import { OrchestratorContextDigestService } from '../../state/orchestrator-context-digest.service';
import { OrchestratorPanelStateService } from '../../state/orchestrator-panel-state.service';
import { ChatSwitcherRailComponent } from '../chat-switcher-rail/chat-switcher-rail.component';
import { OrchestratorChatPaneComponent } from '../orchestrator-chat-pane/orchestrator-chat-pane.component';
import { OrchestratorContextHeaderComponent } from '../orchestrator-context-header/orchestrator-context-header.component';
import { OrchestratorProjectPickerComponent } from '../orchestrator-project-picker/orchestrator-project-picker.component';

interface PinnedContextSnapshot {
  project: string | null;
  jobId: string | null;
  jobTitle: string | null;
  jobKey: string | null;
  jobState: string | null;
  watchPath: string | null;
}

/**
 * Push-layout host for automatic context-keyed orchestrator chats. Context,
 * pinning and the optional multichat rail live here; transcript transport and
 * composer behavior live in {@link OrchestratorChatPaneComponent}.
 */
@Component({
  selector: 'app-orchestrator-side-sheet',
  standalone: true,
  imports: [
    SidesheetComponent,
    ChatSwitcherRailComponent,
    OrchestratorChatPaneComponent,
    OrchestratorContextHeaderComponent,
    OrchestratorProjectPickerComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-side-sheet.component.html',
  styleUrl: './orchestrator-side-sheet.component.scss',
  providers: [OrchestratorContextDigestService],
  host: {
    '[class.is-open]': 'open()',
    '[style.width]': 'open() ? renderedPanelWidth() : null',
  },
})
export class OrchestratorSideSheetComponent implements OnInit, OnDestroy {
  readonly jobService = inject(TaskService);
  private readonly panelState = inject(OrchestratorPanelStateService);

  readonly projects = input<string[]>([]);
  readonly preferredProject = input<string | null>(null);
  readonly watchPaths = input<WatchPathEntry[]>([]);
  readonly activeJobId = input<string | null>(null);
  readonly activeJobTitle = input<string | null>(null);
  readonly activeWatchPath = input<string | null>(null);
  readonly activeJobKey = input<string | null>(null);
  readonly activeJobState = input<string | null>(null);
  readonly activeRun = input<{ model: string | null; startedAt: string | null } | null>(null);

  readonly createTaskFromDraft = output<{ projectName: string; promptText: string }>();
  readonly openVerboseDebug = output<{ jobId: string; watchPath: string; jobTitle: string | null }>();
  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();
  readonly openSettings = output<void>();
  readonly navigateToContext = output<string>();

  readonly open = signal(false);
  readonly panelWidth = this.panelState.width;
  readonly renderedPanelWidth = computed(() => this.railOpen()
    ? `min(${this.panelWidth() + 230}px, 96vw)`
    : `${this.panelWidth()}px`);
  readonly activeProject = signal<string | null>(null);
  readonly selectedContextKey = signal<string | null>(null);
  readonly contextSessions = signal<OrchestratorContextSession[]>([]);
  readonly railOpen = signal(false);
  readonly pinned = signal(false);
  readonly contextDismissed = signal(false);
  readonly contextDigestState = inject(OrchestratorContextDigestService);
  readonly chatPane = viewChild(OrchestratorChatPaneComponent);

  private readonly pinnedSnapshot = signal<PinnedContextSnapshot | null>(null);
  private readonly seenContexts = signal<Record<string, string>>(this.readSeenContexts());
  private pollTimer: VisibleIntervalHandle | null = null;
  private lastNavigationSignature: string | null = null;

  readonly activeContextCount = computed(() => this.contextSessions()
    .filter(session => session.runtimeStatus === 'active' || session.runtimeStatus === 'queued').length);

  readonly unreadContextKeys = computed<ReadonlySet<string>>(() => {
    const seen = this.seenContexts();
    return new Set(this.contextSessions()
      .filter(session => session.updatedAt
        && session.contextKey !== this.contextKey()
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
    return this.jobService.jobs().find(task => task.projectName === session.projectId
      && (task.taskKey === session.taskKey || task.displayKey === session.taskKey || task.key === session.taskKey)) ?? null;
  });

  readonly effectiveProject = computed<string | null>(() => {
    if (this.selectedContextKey() === 'global') return null;
    return this.selectedSession()?.projectId
      ?? (this.pinned() ? this.pinnedSnapshot()?.project ?? null : this.activeProject());
  });

  readonly effectiveJobId = computed<string | null>(() => {
    if (this.selectedContextKey() === 'global') return null;
    if (this.selectedSession()?.kind === 'task') return this.selectedTask()?.id ?? null;
    if (this.selectedSession()) return null;
    return this.pinned() ? this.pinnedSnapshot()?.jobId ?? null : this.activeJobId();
  });

  readonly effectiveJobTitle = computed<string | null>(() => {
    if (this.selectedContextKey() === 'global') return null;
    if (this.selectedSession()?.kind === 'task') {
      return this.selectedTask()?.title ?? this.selectedSession()?.taskKey ?? null;
    }
    if (this.selectedSession()) return null;
    return this.pinned() ? this.pinnedSnapshot()?.jobTitle ?? null : this.activeJobTitle();
  });

  readonly effectiveJobKey = computed<string | null>(() => {
    if (this.selectedContextKey() === 'global') return null;
    if (this.selectedSession()?.kind === 'task') return this.selectedSession()?.taskKey ?? null;
    if (this.selectedSession()) return null;
    return this.pinned() ? this.pinnedSnapshot()?.jobKey ?? null : this.activeJobKey();
  });

  readonly effectiveJobState = computed<string | null>(() => {
    if (this.selectedContextKey() === 'global') return null;
    if (this.selectedSession()?.kind === 'task') return this.selectedTask()?.state ?? null;
    if (this.selectedSession()) return null;
    return this.pinned() ? this.pinnedSnapshot()?.jobState ?? null : this.activeJobState();
  });

  readonly effectiveWatchPath = computed<string | null>(() => {
    if (this.selectedContextKey() === 'global') return null;
    if (this.selectedSession()?.kind === 'task') return this.selectedTask()?.watchPath ?? null;
    if (this.selectedSession()) return null;
    return this.pinned() ? this.pinnedSnapshot()?.watchPath ?? null : this.activeWatchPath();
  });

  readonly contextKind = computed<'task' | 'project'>(() =>
    this.selectedSession()?.kind === 'task'
      || ((this.effectiveJobId() || this.effectiveJobKey()) && this.effectiveJobTitle())
      ? 'task'
      : 'project');

  readonly contextKey = computed<string | null>(() => {
    const selected = this.selectedContextKey();
    if (selected) return selected;
    const project = this.effectiveProject()?.trim();
    if (!project) return null;
    const taskKey = this.effectiveJobKey()?.trim();
    return this.contextKind() === 'task' && taskKey
      ? `task:${project}/${taskKey}`
      : `project:${project}`;
  });

  readonly contextChipText = computed<string | null>(() => {
    const project = this.effectiveProject();
    if (!project) return null;
    return this.contextKind() === 'task'
      ? `Context: ${project} · Task '${this.effectiveJobTitle()}'`
      : `Context: ${project} · Board`;
  });

  /** Compatibility facade retained for focused MC-2 tests and callers. */
  readonly turns = computed(() => this.chatPane()?.turns() ?? []);

  constructor() {
    effect(() => {
      const key = this.contextKey();
      const isOpen = this.open();
      untracked(() => {
        this.contextDigestState.selectContext(key);
        if (isOpen && key) this.contextDigestState.load(key, false);
      });
    });

    effect(() => {
      this.contextKey();
      this.effectiveJobId();
      untracked(() => {
        if (this.contextDismissed()) this.contextDismissed.set(false);
      });
    });

    effect(() => {
      const preferred = this.preferredProject();
      const projects = this.projects();
      untracked(() => {
        if (this.pinned()) return;
        if (!preferred) {
          if (this.activeProject() == null && projects.length > 0) this.activeProject.set(projects[0]);
          return;
        }
        if (projects.includes(preferred) && preferred !== this.activeProject()) this.activeProject.set(preferred);
      });
    });

    // A rail name click changes only the chat. The next real workspace
    // navigation restores automatic context following unless pinned.
    effect(() => {
      const signature = `${this.preferredProject() ?? ''}|${this.activeJobId() ?? ''}|${this.activeJobKey() ?? ''}`;
      untracked(() => {
        if (this.lastNavigationSignature !== null
          && signature !== this.lastNavigationSignature
          && !this.pinned()) {
          this.selectedContextKey.set(null);
        }
        this.lastNavigationSignature = signature;
      });
    });

    // Viewing a context advances this browser's local read receipt, so it
    // does not become unread immediately after the operator leaves it.
    effect(() => {
      const key = this.contextKey();
      const session = this.contextSessions().find(item => item.contextKey === key);
      const isOpen = this.open();
      untracked(() => {
        if (!isOpen || !key || !session?.updatedAt) return;
        if ((this.seenContexts()[key] ?? '') >= session.updatedAt) return;
        this.seenContexts.update(seen => ({ ...seen, [key]: session.updatedAt }));
        this.persistSeenContexts();
      });
    });
  }

  ngOnInit(): void {
    this.refreshContextSessions();
    this.pollTimer = setVisibleInterval(() => {
      if (this.open()) this.refreshContextSessions();
    }, 15_000);
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearVisibleInterval(this.pollTimer);
    this.pollTimer = null;
  }

  show(): void { this.open.set(true); }

  hide(): void {
    this.railOpen.set(false);
    this.open.set(false);
  }

  toggle(): void {
    if (this.open()) this.hide();
    else this.show();
  }

  toggleRail(): void {
    this.railOpen.update(open => !open);
  }

  setActiveProject(project: string): void {
    if (this.pinned()) return;
    this.selectedContextKey.set(null);
    if (project !== this.activeProject()) this.activeProject.set(project);
  }

  selectProjectTab(project: string): void {
    this.setActiveProject(project);
  }

  selectChatContext(contextKey: string): void {
    this.selectedContextKey.set(contextKey);
    const session = this.contextSessions().find(item => item.contextKey === contextKey);
    const project = session?.projectId
      ?? (contextKey.startsWith('project:') ? contextKey.slice('project:'.length) : null);
    if (project) this.activeProject.set(project);
    this.markSeen(contextKey, session?.updatedAt ?? new Date().toISOString());
  }

  onNavigateToContext(contextKey: string): void {
    this.selectedContextKey.set(null);
    this.navigateToContext.emit(contextKey);
  }

  togglePin(): void {
    if (this.pinned()) {
      this.pinned.set(false);
      this.pinnedSnapshot.set(null);
      this.selectedContextKey.set(null);
      const preferred = this.preferredProject();
      if (preferred && this.projects().includes(preferred)) this.activeProject.set(preferred);
      return;
    }
    this.pinnedSnapshot.set({
      project: this.effectiveProject(),
      jobId: this.effectiveJobId(),
      jobTitle: this.effectiveJobTitle(),
      jobKey: this.effectiveJobKey(),
      jobState: this.effectiveJobState(),
      watchPath: this.effectiveWatchPath(),
    });
    this.pinned.set(true);
  }

  refresh(silent = false): void {
    this.chatPane()?.refresh(silent);
  }

  refreshCurrentContext(): void {
    const key = this.contextKey();
    if (!key || this.contextDigestState.refreshing()) return;
    this.contextDigestState.load(key, true);
    this.refresh(false);
    this.refreshContextSessions();
  }

  toggleNextMessageContext(): void {
    this.contextDismissed.update(dismissed => !dismissed);
  }

  onOpenSettings(): void {
    this.openSettings.emit();
  }

  onOpenVerboseDebug(): void {
    const jobId = this.effectiveJobId();
    const watchPath = this.effectiveWatchPath();
    if (jobId && watchPath) {
      this.openVerboseDebug.emit({ jobId, watchPath, jobTitle: this.effectiveJobTitle() });
    }
  }

  startResize(event: MouseEvent): void {
    event.preventDefault();
    const startX = event.clientX;
    const startWidth = this.panelWidth();
    const onMove = (moveEvent: MouseEvent) => this.panelState.setWidth(startWidth + (startX - moveEvent.clientX));
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

  private refreshContextSessions(): void {
    this.jobService.getOrchestratorContextSessions().subscribe({
      next: response => this.contextSessions.set(response.sessions ?? []),
      error: () => this.contextSessions.set([]),
    });
  }

  private markSeen(contextKey: string, updatedAt: string): void {
    this.seenContexts.update(seen => ({ ...seen, [contextKey]: updatedAt }));
    this.persistSeenContexts();
  }

  private readSeenContexts(): Record<string, string> {
    if (typeof window === 'undefined') return {};
    try {
      return JSON.parse(window.localStorage?.getItem('atp.chatSwitcher.seen.v1') ?? '{}');
    } catch {
      return {};
    }
  }

  private persistSeenContexts(): void {
    if (typeof window === 'undefined') return;
    try {
      window.localStorage?.setItem('atp.chatSwitcher.seen.v1', JSON.stringify(this.seenContexts()));
    } catch {
      // Local read state is optional.
    }
  }
}
