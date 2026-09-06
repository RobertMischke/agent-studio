import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskIntegrationStatus } from '../../features/git';
import { PendingButtonDirective } from '../async-feedback';
import { NotificationService } from '../../services/notification.service';
import { TaskService } from '../../services/task.service';

/**
 * AGT-2202: the accept-safety badge. Renders the honest, git-derived
 * {@link TaskIntegrationStatus} on an accepted card (5-human-review / 6-completed
 * / 7-archive) so "Accept != Merge" is impossible to miss:
 *   - green "merged @sha": every attributed commit is provably integrated,
 *   - orange "partially integrated": some attributed commits are missing,
 *   - amber "NOT integrated": accepted work is not integrated,
 *   - red "Integration failed": the integration step reached a failed outcome,
 *   - grey "no branch": nothing to integrate (read-only or no code).
 *
 * Membership is derived from the SAME attributed `commits[]` list the card's
 * commit widget renders. Branch presence comes from the acceptance resolver's
 * projected `deliveryRef`, so a remote delivery cannot render as "kein Branch".
 *
 * Follows the {@link ExecutionLocationBadgeComponent} pill pattern. Purely
 * presentational; hidden when the card carries no integration verdict.
 */
@Component({
  selector: 'app-integration-status-badge',
  standalone: true,
  imports: [TooltipDirective, PendingButtonDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './integration-status-badge.component.html',
  styleUrl: './integration-status-badge.component.scss',
})
export class IntegrationStatusBadgeComponent {
  readonly integration = input<TaskIntegrationStatus | null | undefined>(null);
  readonly jobId = input<string | null>(null);
  readonly watchPath = input<string | null>(null);
  readonly recoveryPending = signal(false);
  private readonly notifications = inject(NotificationService);
  private readonly tasks = inject(TaskService);

  /** The card renders the badge only when a verdict is present. */
  readonly visible = computed(() => !!this.integration());

  /** Coarse visual kind for colour theming. */
  readonly kind = computed<'integrated' | 'partial' | 'pending' | 'conflict' | 'no-branch'>(() => {
    switch (this.integration()?.status) {
      case 'integrated': return 'integrated';
      case 'partial': return 'partial';
      case 'pending': return 'pending';
      case 'conflict-skipped': return 'conflict';
      default: return 'no-branch';
    }
  });

  /** True for the states that mean "accepted, but the code is NOT (fully) in develop". */
  readonly acute = computed(() => {
    const s = this.integration()?.status;
    return s === 'partial' || s === 'pending' || s === 'conflict-skipped';
  });

  readonly recoveryAvailable = computed(() => {
    const value = this.integration();
    if (value?.status !== 'conflict-skipped') return false;
    // Legacy payloads did not carry a classification and represented only
    // merge conflicts. Preserve their recovery action while new payloads use
    // the explicit capability bit.
    return value.failure?.rebaseRecoveryAvailable ?? true;
  });

  readonly label = computed(() => {
    const value = this.integration();
    if (!value) return '';
    switch (value.status) {
      case 'integrated': return value.sha ? `merged @${value.sha}` : 'merged';
      case 'partial': return 'partially integrated';
      case 'pending': return 'NOT integrated';
      case 'conflict-skipped': return value.failure?.label ?? 'Integration failed';
      default: return 'no branch';
    }
  });

  readonly repositoryLines = computed(() => (this.integration()?.repositories ?? []).map((entry) => {
    const repository = repositoryName(entry.repository);
    const total = entry.commits.length;
    const targets = entry.onIntegrationBranch
      ? entry.onReleaseBranch && entry.releaseBranch !== entry.integrationBranch
        ? `${entry.integrationBranch} and ${entry.releaseBranch}`
        : entry.integrationBranch
      : entry.integrationBranch;
    const reason = entry.onIntegrationBranch
      ? ''
      : ` · ${entry.detail.replace(/^.*?;\s*/, '')}`;
    return {
      key: entry.repository,
      text: `${repository} ${entry.integrationCommitCount}/${total} ${targets}${reason}`,
      detail: entry.detail,
    };
  }));

  readonly glyph = computed(() => {
    switch (this.kind()) {
      case 'integrated': return '✓'; // check
      case 'partial': return '◐';    // half-filled circle
      case 'conflict': return '⚠';   // warning
      case 'pending': return '○';    // hollow circle
      default: return '–';           // en dash
    }
  });

  readonly tooltip = computed(() => {
    const value = this.integration();
    if (!value) return '';
    const branch = value.integrationBranch || 'develop';
    const head = (() => {
      switch (value.status) {
        case 'integrated':
          return value.sha
            ? `Integrated into ${branch} (${value.sha})`
            : `Integrated into ${branch}`;
        case 'partial':
          return `Partially integrated into ${branch}; some attributed commits are NOT in ${branch}`;
        case 'pending':
          return `Accepted, but NOT integrated into ${branch}`;
        case 'conflict-skipped':
          return value.failure?.label
            ? `${value.failure.label}; the work is NOT integrated into ${branch}`
            : `Integration into ${branch} failed; the work is NOT integrated`;
        default:
          return 'No task branch or commit to integrate';
      }
    })();
    return [...new Set([
      head,
      ...this.repositoryLines().map((repository) => repository.text),
      value.failure?.reason,
      value.detail,
    ].filter(Boolean))].join('\n');
  });

  readonly ariaLabel = computed(() => {
    const value = this.integration();
    if (!value) return '';
    const branch = value.integrationBranch || 'develop';
    const status = (() => {
      switch (value.status) {
        case 'integrated': return `Integrated into ${branch}`;
        case 'partial': return `Partially integrated into ${branch}`;
        case 'pending': return `Not integrated into ${branch}`;
        case 'conflict-skipped': return `${value.failure?.label ?? 'Integration failed'}; not integrated into ${branch}`;
        default: return 'No branch to integrate';
      }
    })();
    const repositories = this.repositoryLines().map((repository) => repository.text).join('; ');
    return repositories ? `${status}; ${repositories}` : status;
  });

  queueRecovery(event: Event): void {
    event.stopPropagation();
    const jobId = this.jobId();
    if (!jobId || this.recoveryPending()) return;

    this.recoveryPending.set(true);
    this.tasks.queueIntegrationRecovery(jobId, this.watchPath() ?? undefined).subscribe({
      next: (response) => {
        this.recoveryPending.set(false);
        this.notifications.success(
          `Integration recovery queued: rebase ${response.deliveryRef} onto ${response.integrationBranch}.`,
        );
        this.tasks.refresh(true);
      },
      error: () => {
        this.recoveryPending.set(false);
        this.notifications.error('Could not queue the integration recovery round.');
      },
    });
  }
}

function repositoryName(value: string): string {
  const normalized = (value || 'repository').trim().replace(/[\\/]+$/, '');
  const segment = normalized.split(/[\\/:]/).filter(Boolean).at(-1) ?? normalized;
  return segment.replace(/\.git$/i, '') || 'repository';
}
