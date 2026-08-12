import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';

interface RailRow extends OrchestratorContextSession {
  label: string;
}

@Component({
  selector: 'app-chat-switcher-rail',
  standalone: true,
  templateUrl: './chat-switcher-rail.component.html',
  styleUrl: './chat-switcher-rail.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ChatSwitcherRailComponent {
  readonly sessions = input<readonly OrchestratorContextSession[]>([]);
  readonly projects = input<readonly string[]>([]);
  readonly tasks = input<readonly TaskInfo[]>([]);
  readonly activeContextKey = input<string | null>(null);
  readonly unreadContextKeys = input<ReadonlySet<string>>(new Set());
  readonly contextSelected = output<string>();
  readonly locationRequested = output<string>();

  readonly rows = computed<RailRow[]>(() => {
    const byKey = new Map(this.sessions().map(session => [session.contextKey, session]));
    const empty = (contextKey: string, kind: RailRow['kind'], projectId: string | null, taskKey: string | null): OrchestratorContextSession => ({
      contextKey, kind, projectId, taskKey, updatedAt: '', model: null,
      cumulativeInputTokens: 0, cumulativeOutputTokens: 0,
      cumulativeCacheReadTokens: 0, cumulativeCacheCreationTokens: 0,
      runtimeStatus: 'idle', queuePosition: 0,
    });
    for (const project of this.projects()) {
      const key = `project:${project}`;
      if (!byKey.has(key)) byKey.set(key, empty(key, 'project', project, null));
    }
    return [...byKey.values()].map(session => ({
      ...session,
      label: session.kind === 'global'
        ? 'Monitoring'
        : session.kind === 'project'
          ? (session.projectId ?? session.contextKey)
          : session.kind === 'dossier'
            ? (session.dossierKey ?? session.dossierTitle ?? session.dossierId ?? session.contextKey)
            : this.taskLabel(session),
    }));
  });

  readonly projectRows = computed(() => this.rows().filter(row => row.kind === 'project'));
  readonly taskRows = computed(() => this.rows().filter(row => row.kind === 'task'));
  readonly dossierRows = computed(() => this.rows().filter(row => row.kind === 'dossier'));

  select(contextKey: string): void {
    this.contextSelected.emit(contextKey);
  }

  navigate(contextKey: string, event: Event): void {
    event.stopPropagation();
    this.locationRequested.emit(contextKey);
  }

  tokenLabel(row: RailRow): string | null {
    const total = row.cumulativeInputTokens + row.cumulativeOutputTokens
      + row.cumulativeCacheReadTokens + row.cumulativeCacheCreationTokens;
    if (total <= 0) return null;
    if (total >= 1_000_000) return `${(total / 1_000_000).toFixed(1)}m`;
    if (total >= 1_000) return `${Math.round(total / 1_000)}k`;
    return String(total);
  }

  private taskLabel(session: OrchestratorContextSession): string {
    const task = this.tasks().find(item =>
      item.projectName === session.projectId
      && (item.taskKey === session.taskKey || item.displayKey === session.taskKey || item.key === session.taskKey));
    return task?.title ?? session.taskKey ?? session.contextKey;
  }
}
