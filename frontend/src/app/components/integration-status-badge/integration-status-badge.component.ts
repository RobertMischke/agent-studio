import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskIntegrationStatus } from '../../features/git';
import { PendingButtonDirective } from '../async-feedback';
import { NotificationService } from '../../services/notification.service';
import { TaskService } from '../../services/task.service';

/**
 * AGT-2202 — the accept-safety badge. Renders the honest, git-derived
 * {@link TaskIntegrationStatus} on an accepted card (5-human-review / 6-completed
 * / 7-archive) so "Accept != Merge" is impossible to miss:
 *   - green  "merged @sha"          — every attributed commit is provably in develop,
 *   - orange "partially integrated" — some attributed commits are in develop, some are not,
 *   - amber  "not integrated"       — accepted work is still not in develop,
 *   - red    "Integration failed"   — the integration step reached a failed outcome,
 *   - grey   "no branch"            — nothing to integrate (read-only / no code).
 *
 * Membership is derived from the SAME attributed `commits[]` list the card's
 * commit widget renders. Branch presence comes from the acceptance resolver's
 * projected `deliveryRef`, so a remote delivery cannot render as "no branch".
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

  readonly chips = computed(() => {
    const value = this.integration();
    if (!value) return [];
    if (!value.repositories?.length) {
      return [{
        key: 'legacy',
        kind: this.kindFor(value.status),
        glyph: this.glyphFor(this.kindFor(value.status)),
        label: this.legacyLabel(value),
        tooltip: this.legacyTooltip(value),
        ariaLabel: this.legacyAriaLabel(value),
        detail: value.detail,
      }];
    }
    return value.repositories.map(repository => {
      const total = repository.commits.length;
      const landed = repository.commits.filter(commit => commit.onIntegrationBranch).length;
      const missing = repository.commits
        .filter(commit => !commit.onIntegrationBranch)
        .map(commit => commit.sha.slice(0, 7));
      const status = repository.onIntegrationBranch
        ? 'integrated'
        : landed > 0
          ? 'partial'
          : value.status === 'conflict-skipped' ? 'conflict-skipped' : 'pending';
      const branchText = repository.onReleaseBranch
        && repository.releaseBranch !== repository.integrationBranch
        ? `${repository.integrationBranch} and ${repository.releaseBranch}`
        : repository.integrationBranch;
      const reason = missing.length > 0 ? ` · missing ${missing.join(', ')}` : '';
      return {
        key: repository.repository,
        kind: this.kindFor(status),
        glyph: this.glyphFor(this.kindFor(status)),
        label: `${repository.label} ${landed}/${total} ${branchText}${reason}`,
        tooltip: repository.detail ?? `${landed}/${total} commits on ${repository.integrationBranch}`,
        ariaLabel: `${repository.label}: ${landed} of ${total} commits on ${repository.integrationBranch}`,
        detail: repository.detail,
      };
    });
  });
  readonly label = computed(() => this.chips()[0]?.label ?? '');
  readonly glyph = computed(() => this.chips()[0]?.glyph ?? '');
  readonly tooltip = computed(() => this.chips()[0]?.tooltip ?? '');
  readonly ariaLabel = computed(() => this.chips()[0]?.ariaLabel ?? '');

  /** Coarse visual kind for colour theming. */
  readonly kind = computed<'integrated' | 'partial' | 'pending' | 'conflict' | 'no-branch'>(() => {
    return this.kindFor(this.integration()?.status);
  });

  private kindFor(status: TaskIntegrationStatus['status'] | undefined): 'integrated' | 'partial' | 'pending' | 'conflict' | 'no-branch' {
    switch (status) {
      case 'integrated': return 'integrated';
      case 'partial': return 'partial';
      case 'pending': return 'pending';
      case 'conflict-skipped': return 'conflict';
      default: return 'no-branch';
    }
  }

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

  private legacyLabel(value: TaskIntegrationStatus): string {
    switch (value.status) {
      case 'integrated': return value.sha ? `merged @${value.sha}` : 'merged';
      case 'partial': return 'partially integrated';
      case 'pending': return 'not integrated';
      case 'conflict-skipped': return value.failure?.label ?? 'Integration failed';
      default: return 'no branch';
    }
  }

  private glyphFor(kind: 'integrated' | 'partial' | 'pending' | 'conflict' | 'no-branch'): string {
    switch (kind) {
      case 'integrated': return '✓'; // check
      case 'partial': return '◐';    // half-filled circle
      case 'conflict': return '⚠';   // warning
      case 'pending': return '○';    // hollow circle
      default: return '–';           // en dash
    }
  }

  private legacyTooltip(value: TaskIntegrationStatus): string {
    const branch = value.integrationBranch || 'develop';
    const head = (() => {
      switch (value.status) {
        case 'integrated':
          return value.sha
            ? `Integrated into ${branch} (${value.sha})`
            : `Integrated into ${branch}`;
        case 'partial':
          return `Partially integrated into ${branch} — some attributed commits are NOT in ${branch}`;
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
    return [...new Set([head, value.failure?.reason, value.detail].filter(Boolean))].join('\n');
  }

  private legacyAriaLabel(value: TaskIntegrationStatus): string {
    const branch = value.integrationBranch || 'develop';
    switch (value.status) {
      case 'integrated': return `Integrated into ${branch}`;
      case 'partial': return `Partially integrated into ${branch}`;
      case 'pending': return `Not integrated into ${branch}`;
      case 'conflict-skipped': return `${value.failure?.label ?? 'Integration failed'}; not integrated into ${branch}`;
      default: return 'No branch to integrate';
    }
  }

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
