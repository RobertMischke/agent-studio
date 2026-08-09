import { ChangeDetectionStrategy, Component, input } from '@angular/core';

export interface BuildProfileGateSummary {
  profile: unknown | null;
  gateApplicable: boolean;
  verifyPlan: {
    source: string;
    commands: readonly unknown[];
  };
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
