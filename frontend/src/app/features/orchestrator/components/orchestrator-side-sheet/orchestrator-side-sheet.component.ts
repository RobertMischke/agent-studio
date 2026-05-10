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
import { JobService } from '../../../../services/job.service';
import { setVisibleInterval, clearVisibleInterval, VisibleIntervalHandle } from '../../../../utils/visible-interval';
import type { WatchPathEntry } from '../../../../models/job.model';
import type { OrchestratorChatTurn } from '../../../../features/orchestrator';
import { ChatComponent } from '../../../../components/chat/chat.component';
import { ChatEvent, ChatMessage, ChatSubmitEvent } from '../../../../components/chat/chat-types';
import { RoadmapIntakePanelComponent } from '../../../roadmap/components/roadmap-intake/roadmap-intake-panel.component';
import { ProjectChatListComponent } from '../../../project-chat/components/project-chat-list/project-chat-list.component';

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
  templateUrl: './orchestrator-side-sheet.component.html',
  styleUrl: './orchestrator-side-sheet.component.scss',
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

  /**
   * Slice E: opens the kanban detail panel for the new bug job created
   * via the chat's `/bug` directive. The host (app shell) wires this to
   * its existing `openDetail` flow so the click-through stays in-tab and
   * the detail panel reuses the same fetch / URL-sync path it always has.
   */
  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();

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
  private pollTimer: VisibleIntervalHandle | null = null;

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
    this.pollTimer = setVisibleInterval(() => {
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
    if (this.pollTimer != null) clearVisibleInterval(this.pollTimer);
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

  /**
   * Slice E: lookup table from inline event card id to the job it was
   * filed for. Populated when a `/bug ...` directive succeeds; consumed
   * by {@link onChatEventAction} to open the detail panel of that job
   * without parsing the rendered markdown back out.
   */
  private readonly bugEventTargets = new Map<string, { jobId: string; watchPath: string }>();

  async onSubmit(event: ChatSubmitEvent): Promise<void> {
    const proj = this.activeProject();
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

  /**
   * Slice E: parse `/bug <description>` and create a backlog task via
   * the existing `POST /api/jobs` endpoint. The directive must land in
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
        agent: 'copilot',
        watchPath,
        promptMarkdown,
        targetState: '0-backlog',
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

/**
 * Slice E: parse `#tag1 #tag2` patterns at the start of any line in the
 * `/bug` description. A tag word is `[A-Za-z][\w-]*`; a leading `# ` (with
 * a space) is treated as Markdown heading syntax and skipped, so the
 * common case where the user opens the description with a heading does
 * not capture the heading text as a tag.
 */
function parseBugHashtags(description: string): string[] {
  const found: string[] = [];
  for (const line of description.split('\n')) {
    const trimmed = line.trim();
    if (!/^#[A-Za-z]/.test(trimmed)) continue;
    const matches = trimmed.match(/#[A-Za-z][\w-]*/g);
    if (!matches) continue;
    for (const m of matches) {
      const tag = m.substring(1);
      if (!found.includes(tag)) found.push(tag);
    }
  }
  return found;
}
