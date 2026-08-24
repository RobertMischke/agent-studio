import { ChangeDetectionStrategy, Component, input } from '@angular/core';

/** Response shape of `GET /api/projects/{name}/build-profile`. */
export interface BuildProfileGateSummary {
  profile: unknown | null;
  gateApplicable: boolean;
  verifyPlan: {
    source: string;
    commands: readonly unknown[];
  };
  /** Auto-pickup admission and its cause (AGT-2677). */
  pickupAllowed?: boolean;
  gateReason?: string;
  gateReasonCode?: string;
  /** Ready cards a shut gate is holding back; 0 while the gate is open. */
  readyCardCount?: number;
  /** Directory the validation dry-run runs in; null when the project has no local checkout. */
  validationWorkspace?: string | null;
  revalidationRunsRemaining?: number | null;
}

@Component({
  selector: 'app-project-build-profile-notice',
  standalone: true,
  templateUrl: './project-build-profile-notice.component.html',
  styleUrl: './project-build-profile-notice.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class ProjectBuildProfileNoticeComponent {
  readonly summary = input.required<BuildProfileGateSummary>();
}
