import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import type { TaskInfo } from '../../../../models/task.model';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { OrchestratorContextSession } from '../../models/orchestrator.model';

interface RailRow extends OrchestratorContextSession {
  label: string;
  explanation: string;
}

@Component({
  selector: 'app-chat-switcher-rail',
  standalone: true,
  imports: [AppTooltipDirective],
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
    if (!byKey.has('global')) byKey.set('global', empty('global', 'global', null, null));
    for (const project of this.projects()) {
      const key = `project:${project}`;
      if (!byKey.has(key)) byKey.set(key, empty(key, 'project', project, null));
    }
    return [...byKey.values()].map(session => ({
      ...session,
      label: session.kind === 'global'
        ? 'Global orchestrator'
        : session.kind === 'project'
          ? (session.projectId ?? session.contextKey)
          : this.taskLabel(session),
      explanation: this.contextExplanation(session.kind),
    }));
  });

  readonly globalRows = computed(() => this.rows().filter(row => row.kind === 'global'));
  readonly projectRows = computed(() => this.rows().filter(row => row.kind === 'project'));
  readonly taskRows = computed(() => this.rows().filter(row => row.kind === 'task'));

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

  navigationExplanation(row: RailRow): string {
    if (row.kind === 'global') {
      return 'Return to the workspace view when you want to leave a project or task page.';
    }
    if (row.kind === 'project') {
      return 'Open this project when you want its board and project tools in view.';
    }
    return 'Open this task when you want to inspect or update the work behind this chat.';
  }

  private contextExplanation(kind: RailRow['kind']): string {
    if (kind === 'global') {
      return 'Switch to the workspace-wide chat when your question spans projects and tasks.';
    }
    if (kind === 'project') {
      return "Switch to this project's chat when your question concerns the project beyond one task.";
    }
    return "Switch to this task's chat when your question concerns this specific piece of work.";
  }

  private taskLabel(session: OrchestratorContextSession): string {
    const task = this.tasks().find(item =>
      item.projectName === session.projectId
      && (item.taskKey === session.taskKey || item.displayKey === session.taskKey || item.key === session.taskKey));
    return task?.title ?? session.taskKey ?? session.contextKey;
  }
}
