import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';
import type { RemoteHost } from '../../models/remote-host.model';

type GitStatus = RemoteHost['gitPushStatus'];

@Component({
  selector: 'app-git-token-capability',
  standalone: true,
  imports: [AppTooltipDirective],
  templateUrl: './git-token-capability.html',
  styleUrl: './git-token-capability.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class GitTokenCapabilityComponent {
  readonly status = input<GitStatus>(null);
  readonly detail = input<string | null | undefined>(null);
  readonly mode = input.required<'badges' | 'warning'>();

  readonly contents = computed(() => {
    if (this.status() === 'ready' || this.status() === 'ready-no-workflow-scope') {
      return { label: 'Contents: ok', tone: 'ok' };
    }
    return this.status() === 'read-only'
      ? { label: 'Contents: blocked', tone: 'error' }
      : { label: 'Contents: unknown', tone: 'idle' };
  });
  readonly workflow = computed(() => this.status() === 'ready'
    ? { label: 'Workflow: ok', tone: 'ok' }
    : this.status() === 'ready-no-workflow-scope'
      ? { label: 'Workflow: permission missing', tone: 'warn' }
      : { label: 'Workflow: unknown', tone: 'idle' });
}
