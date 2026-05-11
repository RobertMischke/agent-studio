import { ChangeDetectionStrategy, Component, inject, output } from '@angular/core';
import { ProjectOverlaysService } from '../state/project-overlays.service';
import { OrchestratorFeedComponent } from '../../orchestrator/components/orchestrator-feed';
import { ProjectDetailComponent } from './project-detail';
import { ProjectShellComponent } from './project-shell/project-shell.component';
import { SecurityPanelComponent } from './security-panel/security-panel.component';
import { UxuiPanelComponent } from './uxui-panel/uxui-panel.component';
import { ProjectTokenUsagePanelComponent } from '../../project-token-usage/components/project-token-usage-panel.component';
import { ProjectObservabilityPanelComponent } from './project-observability/project-observability-panel.component';
import { ProjectProductRuntimePanelComponent } from './project-product-runtime/project-product-runtime-panel.component';
import { ProjectSteeringDocsSectionComponent } from './project-steering-docs-section';
import { AnalysisReportDrilldownComponent } from './analysis-report-drilldown';
import { ProjectRailKey } from './project-shell/project-shell.config';
import { WorkspaceScreenshotsComponent } from '../../screenshots';
import type { JobScreenshot } from '../../screenshots';

/**
 * Cycle 9g project-detail-feature container: renders the project-level
 * overlays (orch-feed / project-shell / analysis-report). The former
 * project-detail overlay is mounted inside the project-shell settings
 * rail so settings keep their functionality without a second window.
 * Open/close + URL-hash sync owned by
 * ProjectOverlaysService; the shell only mounts this component once.
 *
 * Per-rail follow-up events bubble up because they trigger
 * create-job-dialog, whose form state lives in the shell.
 */
@Component({
  selector: 'app-project-overlays',
  standalone: true,
  imports: [
    OrchestratorFeedComponent,
    ProjectDetailComponent,
    ProjectShellComponent,
    SecurityPanelComponent,
    UxuiPanelComponent,
    ProjectTokenUsagePanelComponent,
    ProjectObservabilityPanelComponent,
    ProjectProductRuntimePanelComponent,
    ProjectSteeringDocsSectionComponent,
    AnalysisReportDrilldownComponent,
    WorkspaceScreenshotsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-overlays.component.html',
})
export class ProjectOverlaysComponent {
  readonly overlays = inject(ProjectOverlaysService);

  // Per-rail follow-up events bubble up to the shell because they
  // trigger create-job-dialog (form state lives there).
  readonly securityFollowUp = output<{ projectName: string; prefill: string }>();
  readonly securityOpenEvidence = output<{ projectName: string; relPath: string }>();
  readonly securityAuditQueued = output<{ projectName: string; jobId: string }>();
  readonly uxuiFollowUp = output<{ projectName: string; prefill: string; title: string }>();
  readonly uxuiActionQueued = output<{ projectName: string; action: string; jobId: string }>();
  readonly openTask = output<JobScreenshot>();

  /** The shell needs to provide watchPaths for hash → name resolution on rail change. */
  readonly railChangeNeedsWatchPaths = output<ProjectRailKey>();

  hasCustomPanel(rail: ProjectRailKey): boolean {
    return rail === 'security'
      || rail === 'visual-evidence'
      || rail === 'uxui'
      || rail === 'token-usage'
      || rail === 'observability'
      || rail === 'product-runtime'
      || rail === 'steering'
      || rail === 'settings';
  }

  setProjectShellRail(key: ProjectRailKey): void {
    this.overlays.setProjectShellRail(key);
  }
}
