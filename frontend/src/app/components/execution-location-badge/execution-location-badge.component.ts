import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskExecutionLocation } from '../../models/task.model';

@Component({
  selector: 'app-execution-location-badge',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './execution-location-badge.component.html',
  styleUrl: './execution-location-badge.component.scss',
})
export class ExecutionLocationBadgeComponent {
  readonly execution = input<TaskExecutionLocation | null | undefined>(null);
  readonly compact = input(true);
  readonly visible = computed(() => this.execution()?.state !== 'no-active-execution');
  readonly acute = computed(() => this.execution()?.state === 'remote-disconnected' && !this.execution()?.historical);
  readonly label = computed(() => {
    const value = this.execution();
    if (!value) return '';
    if (value.state === 'recovering') return 'Recovering';
    if (value.executionKind === 'local') return 'Local';
    const id = value.runnerId || value.hostDisplayName || value.configuredRunnerId || 'remote';
    return `Remote · ${id}`;
  });
  readonly stateLabel = computed(() => ({
    'local-running': 'Local running',
    'remote-running': 'Remote running',
    'remote-disconnected': 'Remote disconnected or stale',
    'queued-remote': 'Queued for remote',
    'recovering': 'Recovering ownership',
    'no-active-execution': 'No active execution',
  }[this.execution()?.state ?? 'no-active-execution']));
  readonly tooltip = computed(() => {
    const value = this.execution();
    if (!value) return '';
    const actual = value.executionKind === 'none'
      ? 'No active runner'
      : `${value.executionKind === 'local' ? 'Local' : 'Remote'}${value.runnerId ? ` · ${value.runnerId}` : ''}${value.hostDisplayName ? ` (${value.hostDisplayName})` : ''}`;
    const configured = value.configuredRunnerId ? `Remote · ${value.configuredRunnerId}` : 'Local';
    const lines = [
      this.stateLabel(),
      `Actual runner: ${actual}`,
      `Configured routing: ${configured}`,
      value.startedAt ? `Started: ${this.format(value.startedAt)}` : null,
      value.lastHeartbeat ? `Last heartbeat: ${this.format(value.lastHeartbeat)}` : null,
      value.lastActivityAt ? `Last activity: ${this.format(value.lastActivityAt)}` : null,
      value.branch ? `Branch: ${value.branch}` : null,
      value.worktreePath ? `Worktree: ${value.worktreePath}` : null,
      value.processId ? `Process: ${value.processId}` : null,
      value.sessionId ? `Session: ${value.sessionId}` : null,
      `Connection: ${value.connectionState}; lease: ${value.leaseState}`,
      `Trusted because: ${value.trustReason}`,
    ];
    return lines.filter(Boolean).join('\n');
  });

  private format(value: string): string {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? value : date.toLocaleString();
  }
}
