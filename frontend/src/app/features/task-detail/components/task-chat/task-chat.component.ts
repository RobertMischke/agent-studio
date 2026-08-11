import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
  untracked,
} from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ChatComponent } from 'coding-agent-chat/composer';
import { ConversationViewComponent } from 'coding-agent-chat/conversation';
import type { ChatSubmitEvent, ChatToolbarItem } from 'coding-agent-chat/core';
import type { TaskDetail } from '../../../../models/task.model';
import { TaskService } from '../../../../services/task.service';
import {
  OrchestratorContextReceiptComponent,
  buildChatNavigationContext,
  buildOrchestratorContextEnvelope,
  buildOrchestratorConversationEvents,
  type OrchestratorChatTurn,
} from '../../../orchestrator';

/**
 * Task-scoped Orchestrator Q&A. The Task Server transcript is the
 * storage owner; opening this component materializes the managed task context.
 * Task-agent start, stop, and continue endpoints are intentionally absent.
 */
@Component({
  selector: 'app-task-chat',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [
    ChatComponent,
    ConversationViewComponent,
    OrchestratorContextReceiptComponent,
  ],
  templateUrl: './task-chat.component.html',
  styleUrl: './task-chat.component.scss',
})
export class TaskChatComponent {
  readonly detail = input.required<TaskDetail>();

  private readonly tasks = inject(TaskService);
  private requestVersion = 0;

  readonly turns = signal<OrchestratorChatTurn[]>([]);
  readonly loading = signal(false);
  readonly sending = signal(false);
  readonly error = signal<string | null>(null);

  readonly taskKey = computed(() =>
    this.detail().info.displayKey?.trim()
    || this.detail().info.key?.trim()
    || this.detail().info.taskKey,
  );
  readonly projectName = computed(() => this.detail().info.projectName);
  readonly contextKey = computed(() =>
    `task:${this.projectName()}/${this.taskKey()}`,
  );
  readonly latestContextReceipt = computed(() =>
    [...this.turns()].reverse()
      .find(turn => turn.role === 'orchestrator' && turn.contextReceipt)
      ?.contextReceipt ?? null,
  );
  readonly conversationEvents = computed(() =>
    buildOrchestratorConversationEvents(
      this.turns(),
      [],
      [],
      this.projectName(),
      this.contextKey(),
    ),
  );
  readonly placeholder = computed(() => `Ask about ${this.taskKey()}`);
  readonly composerToolbar: readonly ChatToolbarItem[] = [];

  constructor() {
    effect(() => {
      const contextKey = this.contextKey();
      untracked(() => {
        this.turns.set([]);
        this.error.set(null);
        this.sending.set(false);
        void this.loadTranscript(contextKey);
      });
    });
  }

  async onSubmit(event: ChatSubmitEvent): Promise<void> {
    if (this.sending()) return;
    const text = event.text.trim();
    if (!text && event.attachments.length === 0) return;

    const contextKey = this.contextKey();
    const projectName = this.projectName();
    const taskKey = this.taskKey();
    const detail = this.detail();
    const capturedAt = new Date();
    const localTurnId = `task-chat-local-${capturedAt.getTime()}`;
    const displayText = text || '(attachments)';
    this.turns.update(turns => [
      ...turns,
      {
        id: localTurnId,
        ts: capturedAt.toISOString(),
        role: 'user',
        text: displayText,
      },
    ]);
    this.sending.set(true);
    this.error.set(null);

    try {
      const attachments = await Promise.all(event.attachments.map(async attachment => {
        const uploaded = await firstValueFrom(
          this.tasks.uploadOrchestratorChatAttachment(projectName, attachment.file),
        );
        return { alt: attachment.alt, relativePath: uploaded.relativePath };
      }));
      const navigationContext = buildChatNavigationContext({
        activeJobId: detail.info.id,
        activeTaskKey: taskKey,
        activeJobTitle: detail.info.title,
        activeJobState: detail.info.state,
        observedSurface: 'Task Chat',
        now: () => capturedAt,
      });

      await firstValueFrom(this.tasks.sendOrchestratorChatByContext(contextKey, {
        text: displayText,
        attachments: attachments.length > 0 ? attachments : undefined,
        navigationContext,
        contextEnvelope: buildOrchestratorContextEnvelope(
          contextKey,
          navigationContext,
          [],
          null,
          () => capturedAt,
        ),
      }));
      await this.loadTranscript(contextKey, false);
    } catch (cause) {
      if (this.contextKey() !== contextKey) return;
      const message = this.errorMessage(cause, 'Failed to send task question');
      this.error.set(message);
      this.turns.update(turns => turns.map(turn =>
        turn.id === localTurnId ? { ...turn, errorMessage: message } : turn,
      ));
    } finally {
      this.sending.set(false);
    }
  }

  private async loadTranscript(contextKey: string, showLoading = true): Promise<void> {
    const version = ++this.requestVersion;
    if (showLoading) this.loading.set(true);
    try {
      const response = await firstValueFrom(
        this.tasks.getOrchestratorChatByContext(contextKey),
      );
      if (version !== this.requestVersion || this.contextKey() !== contextKey) return;
      this.turns.set(response.turns ?? []);
      this.error.set(null);
    } catch (cause) {
      if (version !== this.requestVersion || this.contextKey() !== contextKey) return;
      this.error.set(this.errorMessage(cause, 'Failed to load Task Chat'));
    } finally {
      if (version === this.requestVersion && this.contextKey() === contextKey) {
        this.loading.set(false);
      }
    }
  }

  private errorMessage(cause: unknown, fallback: string): string {
    const error = cause as {
      error?: { error?: string; detail?: string } | string;
      message?: string;
    };
    if (typeof error?.error === 'string' && error.error.trim()) return error.error;
    if (typeof error?.error === 'object') {
      return error.error.error?.trim()
        || error.error.detail?.trim()
        || error.message?.trim()
        || fallback;
    }
    return error?.message?.trim() || fallback;
  }
}
