import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  output,
  signal,
  untracked,
} from '@angular/core';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { JobsHubClient } from '../../../../services/jobs-hub-client.service';
import { TaskService } from '../../../../services/task.service';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';
import { TooltipDirective } from 'coding-agent-chat/shared';

/**
 * Workspace-wide projection of the Task Server context store. The component
 * owns no transcript or summary state beyond the current GET response.
 */
@Component({
  selector: 'app-orchestrator-chat-history',
  standalone: true,
  imports: [StudioIconComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-chat-history.component.html',
  styleUrl: './orchestrator-chat-history.component.scss',
})
export class OrchestratorChatHistoryComponent {
  private readonly tasks = inject(TaskService);
  readonly hub = inject(JobsHubClient);

  readonly contextOpened = output<string>();
  readonly contexts = signal<readonly OrchestratorContextSession[]>([]);
  readonly loading = signal(true);
  readonly error = signal<string | null>(null);

  readonly projectContexts = computed(() => this.contexts()
    .filter(context => context.kind === 'project')
    .sort((left, right) => left.projectId!.localeCompare(right.projectId!)));
  readonly taskContexts = computed(() => this.contexts()
    .filter(context => context.kind === 'task')
    .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt)));
  readonly dossierContexts = computed(() => this.contexts()
    .filter(context => context.kind === 'dossier')
    .sort((left, right) => right.updatedAt.localeCompare(left.updatedAt)));

  private requestVersion = 0;

  constructor() {
    effect(() => {
      this.hub.orchestratorContextsRevision();
      untracked(() => this.load(true));
    });
  }

  refresh(): void {
    this.load(false);
  }

  open(context: OrchestratorContextSession): void {
    this.contextOpened.emit(context.contextKey);
  }

  contextTitle(context: OrchestratorContextSession): string {
    if (context.kind === 'task') return context.taskKey ?? context.contextKey;
    if (context.kind === 'dossier')
      return context.dossierKey ?? context.dossierTitle ?? context.dossierId ?? context.contextKey;
    return context.projectId ?? context.contextKey;
  }

  contextKindLabel(context: OrchestratorContextSession): string {
    if (context.kind === 'task') return 'Task chat';
    if (context.kind === 'dossier') return 'Dossier chat';
    return 'Project chat';
  }

  activityLabel(value: string): string {
    const timestamp = Date.parse(value);
    if (!Number.isFinite(timestamp)) return 'Unknown';
    const seconds = Math.max(0, Math.floor((Date.now() - timestamp) / 1000));
    if (seconds < 60) return 'Just now';
    const minutes = Math.floor(seconds / 60);
    if (minutes < 60) return `${minutes}m ago`;
    const hours = Math.floor(minutes / 60);
    if (hours < 24) return `${hours}h ago`;
    const days = Math.floor(hours / 24);
    if (days < 7) return `${days}d ago`;
    return new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' }).format(new Date(timestamp));
  }

  activityTitle(value: string): string {
    const timestamp = Date.parse(value);
    return Number.isFinite(timestamp)
      ? new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })
        .format(new Date(timestamp))
      : 'Unknown activity time';
  }

  private load(silent: boolean): void {
    const version = ++this.requestVersion;
    if (!silent || this.contexts().length === 0) this.loading.set(true);
    this.tasks.getOrchestratorContextSessions().subscribe({
      next: response => {
        if (version !== this.requestVersion) return;
        this.contexts.set((response.sessions ?? []).filter(context => context.kind !== 'global'));
        this.error.set(null);
        this.loading.set(false);
      },
      error: () => {
        if (version !== this.requestVersion) return;
        this.error.set('Chat History could not be loaded from the Task Server.');
        this.loading.set(false);
      },
    });
  }
}
