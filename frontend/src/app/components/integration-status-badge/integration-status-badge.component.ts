import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { TaskIntegrationStatus } from '../../features/git';

/**
 * AGT-2202 — the accept-safety badge. Renders the honest, git-derived
 * {@link TaskIntegrationStatus} on an accepted card (5-human-review / 6-completed
 * / 7-archive) so "Accept != Merge" is impossible to miss:
 *   - green  "merged @sha"       — the work is provably in develop,
 *   - amber  "NICHT integriert"  — accepted work is still not in develop,
 *   - red    "Konflikt"          — the merge-into-develop step hit a conflict/skip,
 *   - grey   "kein Branch"       — nothing to integrate (read-only / no code).
 *
 * Follows the {@link ExecutionLocationBadgeComponent} pill pattern. Purely
 * presentational; hidden when the card carries no integration verdict.
 */
@Component({
  selector: 'app-integration-status-badge',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './integration-status-badge.component.html',
  styleUrl: './integration-status-badge.component.scss',
})
export class IntegrationStatusBadgeComponent {
  readonly integration = input<TaskIntegrationStatus | null | undefined>(null);

  /** The card renders the badge only when a verdict is present. */
  readonly visible = computed(() => !!this.integration());

  /** Coarse visual kind for colour theming. */
  readonly kind = computed<'integrated' | 'pending' | 'conflict' | 'no-branch'>(() => {
    switch (this.integration()?.status) {
      case 'integrated': return 'integrated';
      case 'pending': return 'pending';
      case 'conflict-skipped': return 'conflict';
      default: return 'no-branch';
    }
  });

  /** True for the states that mean "accepted, but the code is NOT in develop". */
  readonly acute = computed(() => {
    const s = this.integration()?.status;
    return s === 'pending' || s === 'conflict-skipped';
  });

  readonly label = computed(() => {
    const value = this.integration();
    if (!value) return '';
    switch (value.status) {
      case 'integrated': return value.sha ? `merged @${value.sha}` : 'merged';
      case 'pending': return 'NICHT integriert';
      case 'conflict-skipped': return 'Konflikt';
      default: return 'kein Branch';
    }
  });

  readonly glyph = computed(() => {
    switch (this.kind()) {
      case 'integrated': return '✓'; // check
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
        case 'pending':
          return `Accepted, but NOT integrated into ${branch}`;
        case 'conflict-skipped':
          return `Merge into ${branch} hit a conflict — the work is NOT integrated`;
        default:
          return 'No task branch or commit to integrate';
      }
    })();
    return [head, value.detail].filter(Boolean).join('\n');
  });

  readonly ariaLabel = computed(() => {
    const value = this.integration();
    if (!value) return '';
    const branch = value.integrationBranch || 'develop';
    switch (value.status) {
      case 'integrated': return `Integrated into ${branch}`;
      case 'pending': return `Not integrated into ${branch}`;
      case 'conflict-skipped': return `Merge conflict; not integrated into ${branch}`;
      default: return 'No branch to integrate';
    }
  });
}
