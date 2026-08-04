import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskInfo } from '../../../../models/task.model';
import { providerAuthWaitReason, RemoteHostsService } from '../../../remote-hosts';
import { taskCardNow } from '../task-card/task-card-clock';
import { buildQuotaWaitBadge } from '../task-card/task-card-view-model';

@Component({
  selector: 'app-task-card-provider-auth-wait',
  standalone: true,
  imports: [TooltipDirective],
  templateUrl: './task-card-provider-auth-wait.component.html',
  styleUrl: './task-card-provider-auth-wait.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class TaskCardProviderAuthWaitComponent {
  readonly task = input.required<TaskInfo>();
  private readonly remoteHosts = inject(RemoteHostsService);
  readonly quotaWait = computed(() => {
    const task = this.task();
    return buildQuotaWaitBadge(task.state === '3-progress' ? task.quotaWait : null, taskCardNow());
  });
  readonly wait = computed(() => providerAuthWaitReason(
    this.task(),
    this.remoteHosts.hosts(),
    taskCardNow(),
  ));
}
