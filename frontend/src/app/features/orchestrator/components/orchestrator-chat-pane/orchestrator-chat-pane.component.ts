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
import { ChatComponent } from 'coding-agent-chat/composer';
import { ConversationViewComponent } from 'coding-agent-chat/conversation';
import type { ChatEvent, ChatSubmitEvent, ChatToolbarItem } from 'coding-agent-chat/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { WatchPathEntry } from '../../../../models/task.model';
import { TaskState } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import { clearVisibleInterval, setVisibleInterval, type VisibleIntervalHandle } from '../../../../utils/visible-interval';
import { buildChatNavigationContext } from '../../chat-navigation-context';
import type { OrchestratorChatTurn } from '../../models/orchestrator.model';
import {
  buildDemoEvents,
  buildOrchestratorConversationEvents,
  type OptimisticOrchestratorChatTurn,
  parseBugHashtags,
  readFileAsBase64,
  resolveAttachmentUrl,
} from '../orchestrator-side-sheet/orchestrator-side-sheet.util';

/**
 * Chat-specific half of the orchestrator side sheet. The parent owns context
 * selection and rail layout; this component owns transcript transport,
 * composition, attachments and chat-to-task handoff.
 */
@Component({
  selector: 'app-orchestrator-chat-pane',
  standalone: true,
  imports: [ChatComponent, ConversationViewComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-chat-pane.component.html',
  styleUrl: './orchestrator-chat-pane.component.scss',
})
export class OrchestratorChatPaneComponent implements OnInit, OnDestroy {
  private readonly taskService = inject(TaskService);

  readonly active = input(false);
  readonly project = input<string | null>(null);
  readonly contextKey = input<string | null>(null);
  readonly jobId = input<string | null>(null);
  readonly jobTitle = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly watchPaths = input<readonly WatchPathEntry[]>([]);
  readonly contextExcluded = input(false);

  readonly createTaskFromDraft = output<{ projectName: string; promptText: string }>();
  readonly openVerboseDebug = output<{ jobId: string; watchPath: string; jobTitle: string | null }>();
  readonly openJobDetail = output<{ jobId: string; watchPath: string }>();

  readonly turns = signal<OrchestratorChatTurn[]>([]);
  readonly loading = signal(false);
  readonly sending = signal(false);
  readonly errorMsg = signal<string | null>(null);
  private readonly localTurns = signal<OptimisticOrchestratorChatTurn[]>([]);
  readonly events = signal<ChatEvent[]>([]);
  private readonly bugEventTargets = new Map<string, { jobId: string; watchPath: string }>();

  readonly toolbarStart: readonly ChatToolbarItem[] = [
    { id: 'reference', glyph: '#', label: 'Reference a task' },
    { id: 'mention', glyph: '@', label: 'Mention a participant' },
    { id: 'fork', glyph: '⑂', label: 'Fork into a new thread' },
    { id: 'search', glyph: '🔍', label: 'Search chat history' },
  ];
  readonly toolbarEnd: readonly ChatToolbarItem[] = [
    { id: 'task', glyph: '/task', label: 'Open Add Task pre-filled with the draft', variant: 'pill' },
  ];

  readonly routingLabel = computed<string | null>(() => {
    if (typeof window === 'undefined') return null;
    try {
      const cli = window.localStorage?.getItem('defaultCliType');
      return cli ? `routing: ${cli}` : null;
    } catch {
      return null;
    }
  });

  readonly conversationEvents = computed(() => buildOrchestratorConversationEvents(
    this.turns(),
    this.localTurns(),
    this.events(),
    this.project(),
    this.contextKey() ?? this.project() ?? 'orchestrator-chat',
  ));

  readonly canCreateTaskFromReply = computed(() =>
    [...this.turns()].reverse().some(turn => turn.role === 'orchestrator' && !!turn.text && !turn.errorMessage));

  readonly canCreateTaskFromUserMessage = computed(() =>
    [...this.turns(), ...this.localTurns()].reverse()
      .some(turn => turn.role === 'user' && !!turn.text?.trim()));

  private pollTimer: VisibleIntervalHandle | null = null;
  private lastSentContextSignature: string | null = null;

  constructor() {
    effect(() => {
      const active = this.active();
      const project = this.project();
      const contextKey = this.contextKey();
      untracked(() => {
        this.localTurns.set([]);
        if (active && project && contextKey !== 'global') this.refresh(false);
      });
    });
  }

  ngOnInit(): void {
    this.pollTimer = setVisibleInterval(() => {
      if (this.active() && this.project() && !this.loading() && !this.sending()) this.refresh(true);
    }, 30_000);
    this.seedDemoEvents();
  }

  ngOnDestroy(): void {
    if (this.pollTimer != null) clearVisibleInterval(this.pollTimer);
    this.pollTimer = null;
  }

  refresh(silent = false): void {
    const project = this.project();
    if (!project) return;
    if (!silent) this.loading.set(true);
    this.readChat(project).subscribe({
      next: response => {
        this.turns.set(response.turns ?? []);
        this.errorMsg.set(null);
        if (!silent) this.loading.set(false);
      },
      error: error => {
        this.errorMsg.set(error?.error?.error || error?.message || 'Failed to load orchestrator chat');
        if (!silent) this.loading.set(false);
      },
    });
  }

  async onSubmit(event: ChatSubmitEvent): Promise<void> {
    const project = this.project();
    if (!project) return;
    const text = event.text.trim();
    if (!text && event.attachments.length === 0) return;
    if (text === '/bug' || text.startsWith('/bug ') || text.startsWith('/bug\n')) {
      this.handleBugDirective(text, event, project);
      return;
    }

    const localId = `local:${Date.now()}`;
    this.localTurns.update(turns => [...turns, {
      id: localId,
      ts: new Date().toISOString(),
      role: 'user',
      text: text || '(attachments)',
      pending: true,
      localAttachments: event.attachments.length > 0
        ? event.attachments.map(attachment => ({ alt: attachment.alt, previewUrl: attachment.previewUrl }))
        : undefined,
    }]);
    this.sending.set(true);

    const uploaded: {
      alt: string;
      relativePath: string;
      inlineBase64?: string | null;
      mimeType?: string | null;
    }[] = [];
    try {
      for (const attachment of event.attachments) {
        const [response, inline] = await Promise.all([
          this.uploadOne(project, attachment.file),
          readFileAsBase64(attachment.file).catch(() => null),
        ]);
        uploaded.push({
          alt: attachment.alt,
          relativePath: response.relativePath,
          inlineBase64: inline?.base64 ?? null,
          mimeType: inline?.mimeType ?? attachment.file.type ?? null,
        });
      }
    } catch (error) {
      this.failLocalTurn(localId, (error as { message?: string })?.message ?? 'Attachment upload failed');
      return;
    }

    const signature = `${project}|${this.contextKey() ?? ''}|${this.jobId() ?? ''}|${this.jobTitle() ?? ''}`;
    const includeContext = !this.contextExcluded() && signature !== this.lastSentContextSignature;
    const body = {
      text: text || '(attachments)',
      attachments: uploaded.length > 0 ? uploaded : undefined,
      navigationContext: includeContext
        ? buildChatNavigationContext({ activeJobId: this.jobId(), activeJobTitle: this.jobTitle() })
        : null,
    };
    const key = this.contextKey();
    const send = key
      ? this.taskService.sendOrchestratorChatByContext(key, body)
      : this.taskService.sendOrchestratorChat(project, body);
    send.subscribe({
      next: () => {
        if (includeContext) this.lastSentContextSignature = signature;
        this.sending.set(false);
        this.replaceLocalTurns(project, event, uploaded);
      },
      error: error => this.failLocalTurn(
        localId,
        error?.error?.error || error?.message || 'Failed to send',
      ),
    });
  }

  onOpenVerboseDebug(): void {
    const jobId = this.jobId();
    const watchPath = this.watchPath();
    if (jobId && watchPath) this.openVerboseDebug.emit({ jobId, watchPath, jobTitle: this.jobTitle() });
  }

  onChatEventAction(eventId: string): void {
    const target = this.bugEventTargets.get(eventId);
    if (target) this.openJobDetail.emit(target);
  }

  hasChatEventAction(eventId: string): boolean {
    return this.bugEventTargets.has(eventId);
  }

  onToolbarAction(action: { id: string }): void {
    if (action.id === 'task') this.createFromUserMessage();
  }

  createFromLastReply(): void {
    const project = this.project();
    const turn = [...this.turns()].reverse()
      .find(item => item.role === 'orchestrator' && !!item.text && !item.errorMessage);
    if (project && turn) this.createTaskFromDraft.emit({ projectName: project, promptText: turn.text });
  }

  createFromUserMessage(): void {
    const project = this.project();
    const turn = [...this.turns(), ...this.localTurns()].reverse()
      .find(item => item.role === 'user' && !!item.text?.trim());
    if (project && turn) this.createTaskFromDraft.emit({ projectName: project, promptText: turn.text });
  }

  private readChat(project: string) {
    const key = this.contextKey();
    return key
      ? this.taskService.getOrchestratorChatByContext(key)
      : this.taskService.getOrchestratorChat(project);
  }

  private replaceLocalTurns(
    project: string,
    event: ChatSubmitEvent,
    uploaded: readonly { relativePath: string }[],
  ): void {
    const preloads = uploaded.map(item => new Promise<void>(resolve => {
      const image = new Image();
      image.onload = () => resolve();
      image.onerror = () => resolve();
      image.src = resolveAttachmentUrl(project, item.relativePath);
    }));
    this.readChat(project).subscribe({
      next: async response => {
        this.turns.set(response.turns ?? []);
        this.errorMsg.set(null);
        if (preloads.length > 0) {
          await Promise.race([Promise.all(preloads), new Promise<void>(resolve => setTimeout(resolve, 3000))]);
        }
        this.clearLocalTurns(event);
      },
      error: () => this.clearLocalTurns(event),
    });
  }

  private clearLocalTurns(event: ChatSubmitEvent): void {
    this.localTurns.set([]);
    for (const attachment of event.attachments) URL.revokeObjectURL(attachment.previewUrl);
  }

  private failLocalTurn(localId: string, message: string): void {
    this.sending.set(false);
    this.localTurns.update(turns => turns.map(turn =>
      turn.id === localId ? { ...turn, pending: false, errorMessage: message } : turn));
  }

  private uploadOne(project: string, file: File): Promise<{ relativePath: string; url: string }> {
    return new Promise((resolve, reject) => this.taskService.uploadOrchestratorChatAttachment(project, file).subscribe({
      next: response => resolve({ relativePath: response.relativePath, url: response.url }),
      error: error => reject(new Error(error?.error?.error || error?.message || 'Upload failed')),
    }));
  }

  private handleBugDirective(text: string, event: ChatSubmitEvent, project: string): void {
    const description = text.replace(/^\/bug\s*/, '').trim();
    this.localTurns.update(turns => [...turns, {
      id: `bug-local:${Date.now()}`,
      ts: new Date().toISOString(),
      role: 'user',
      text,
    }]);
    for (const attachment of event.attachments) URL.revokeObjectURL(attachment.previewUrl);
    if (!description) {
      this.appendBugEvent('error', 'Bug not filed: description is empty',
        'Add a description after `/bug`, e.g. `/bug Frontend chips overlap on narrow viewport`.');
      return;
    }
    const watchPath = this.watchPaths().find(item => item.name === project)?.path;
    if (!watchPath) {
      this.appendBugEvent('error', 'Bug not filed: no watch path for this project',
        `Could not resolve a watch path for project \`${project}\`. Check the workspace configuration.`);
      return;
    }
    const tags = parseBugHashtags(description);
    const firstLine = description.split('\n')[0].trim();
    const title = firstLine.length > 80 ? `${firstLine.slice(0, 77)}...` : firstLine;
    this.taskService.createJob({
      title,
      agent: 'claude',
      watchPath,
      promptMarkdown: `${description}\n\n---\n\nReported via /bug from project chat`,
      targetState: TaskState.Backlog,
      taskType: 'bug',
      tags: tags.length > 0 ? tags : undefined,
    }).subscribe({
      next: response => {
        const eventId = `bug-ok:${response.id}`;
        this.bugEventTargets.set(eventId, { jobId: response.id, watchPath });
        const tagSuffix = tags.length > 0
          ? `\n\nTags: ${tags.map(tag => `\`${tag}\``).join(' ')}`
          : '';
        this.events.update(events => [...events, {
          id: eventId,
          kind: 'task',
          timestamp: new Date().toISOString(),
          summary: `Bug filed in 0-backlog: ${title}`,
          detail:
            `**Lane:** \`0-backlog\`  \n`
            + `**Task type:** \`bug\`  \n`
            + `**Job ID:** \`${response.id}\`${tagSuffix}\n\n`
            + 'The new task is in triage. Open the detail panel to refine the prompt before promoting it to `2-ready`.',
          actionLabel: 'Open task',
        }]);
        this.taskService.refresh(true);
      },
      error: error => this.appendBugEvent(
        'error',
        `Bug not filed: ${title || '(empty title)'}`,
        `**Error:** ${error?.error?.error
          || (typeof error?.error === 'string' ? error.error : null)
          || error?.message
          || 'Failed to file bug'}`,
      ),
    });
  }

  private appendBugEvent(severity: 'error', summary: string, detail: string): void {
    this.events.update(events => [...events, {
      id: `bug-error:${Date.now()}`,
      kind: 'task',
      timestamp: new Date().toISOString(),
      severity,
      summary,
      detail,
    }]);
  }

  private seedDemoEvents(): void {
    if (typeof window === 'undefined') return;
    if (new URLSearchParams(window.location.search).get('demoEvents') === '1') {
      this.events.set(buildDemoEvents(Date.now()));
    }
  }
}
