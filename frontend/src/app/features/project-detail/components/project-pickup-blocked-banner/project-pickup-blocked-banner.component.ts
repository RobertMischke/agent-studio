import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { NotificationComponent } from '../../../../components/notification/notification.component';

/** The part of `GET /api/projects/{name}/build-profile` this banner reads. */
export interface ProjectPickupGateSummary {
  pickupAllowed?: boolean;
  gateReason?: string;
  gateReasonCode?: string;
  /** Ready cards a shut gate is holding back; the server counts them. */
  readyCardCount?: number;
  validationWorkspace?: string | null;
  revalidationRunsRemaining?: number | null;
}

/**
 * AGT-2677: a project whose build-profile gate is shut cannot be auto-picked by
 * any runner. That used to be visible only in the backend log, so 25 Ready cards
 * waited five days behind a `declared` status nobody could see. The gate now
 * says so on the project itself, in the panel that owns the build profile.
 *
 * The banner names the count of Ready cards the operator is actually holding, so
 * it reads as "N ready cards not claimable" rather than an abstract status.
 */
@Component({
  selector: 'app-project-pickup-blocked-banner',
  standalone: true,
  imports: [NotificationComponent],
  templateUrl: './project-pickup-blocked-banner.component.html',
  styleUrl: './project-pickup-blocked-banner.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectPickupBlockedBannerComponent {
  readonly gate = input.required<ProjectPickupGateSummary>();

  readonly blocked = computed(() => this.gate().pickupAllowed === false);
  readonly readyCardCount = computed(() => this.gate().readyCardCount ?? 0);

  /**
   * The revalidation grace is the one open-gate state worth a quiet heads-up:
   * pickup still works, but it stops after a bounded number of runs.
   */
  readonly graceRunsLeft = computed(() => {
    const gate = this.gate();
    return gate.pickupAllowed && gate.gateReasonCode === 'revalidation-pending'
      ? (gate.revalidationRunsRemaining ?? 0)
      : 0;
  });
}
