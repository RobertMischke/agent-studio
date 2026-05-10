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

/**
 * Cycle 9g project-detail-feature container: renders the four
 * per-project overlays (orch-feed / project-detail / project-shell /
 * analysis-report). Open/close + URL-hash sync owned by
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
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (overlays.orchFeedProject(); as proj) {
      <div class="overlay" (click)="overlays.closeOrchFeed()">
        <div class="overlay__panel" (click)="$event.stopPropagation()">
          <button class="overlay__close" (click)="overlays.closeOrchFeed()" title="Close">×</button>
          <app-orchestrator-feed [projectName]="proj" />
        </div>
      </div>
    }

    @if (overlays.projectDetailName(); as proj) {
      <div class="overlay" (click)="overlays.closeProjectDetail()">
        <div class="overlay__panel" (click)="$event.stopPropagation()">
          <button class="overlay__close" (click)="overlays.closeProjectDetail()" title="Close">×</button>
          <app-project-detail
            [projectName]="proj"
            (openFeed)="overlays.openFeedFromDetail($event)"
            (openReport)="overlays.openAnalysisReport(proj, $event.reportId)" />
        </div>
      </div>
    }

    @if (overlays.projectShellName(); as projShell) {
      <div class="overlay overlay--shell" data-testid="project-shell-overlay">
        <div class="overlay__shell-panel">
          <app-project-shell
            [projectName]="projShell"
            [activeRail]="overlays.projectShellRail()"
            [hasCustomPanel]="hasCustomPanel(overlays.projectShellRail())"
            (railChange)="setProjectShellRail($event)"
            (openFeed)="overlays.openFeedFromShell()"
            (closeShell)="overlays.closeProjectShell()">
            @defer (when overlays.projectShellRail() === 'security') {
              @if (overlays.projectShellRail() === 'security') {
                <app-security-panel
                  [projectName]="projShell"
                  (createFollowUp)="securityFollowUp.emit($event)"
                  (openEvidence)="securityOpenEvidence.emit($event)"
                  (auditQueuedEvent)="securityAuditQueued.emit($event)" />
              }
            }
            @defer (when overlays.projectShellRail() === 'uxui') {
              @if (overlays.projectShellRail() === 'uxui') {
                <app-uxui-panel
                  [projectName]="projShell"
                  (createFollowUp)="uxuiFollowUp.emit($event)"
                  (actionQueuedEvent)="uxuiActionQueued.emit($event)" />
              }
            }
            @defer (when overlays.projectShellRail() === 'token-usage') {
              @if (overlays.projectShellRail() === 'token-usage') {
                <app-project-token-usage-panel [projectName]="projShell" />
              }
            }
            @defer (when overlays.projectShellRail() === 'observability') {
              @if (overlays.projectShellRail() === 'observability') {
                <app-project-observability-panel [projectName]="projShell" />
              }
            }
            @defer (when overlays.projectShellRail() === 'product-runtime') {
              @if (overlays.projectShellRail() === 'product-runtime') {
                <app-project-product-runtime-panel [projectName]="projShell" />
              }
            }
            @defer (when overlays.projectShellRail() === 'steering') {
              @if (overlays.projectShellRail() === 'steering') {
                <app-project-steering-docs-section [projectName]="projShell" />
              }
            }
          </app-project-shell>
        </div>
      </div>
    }

    @if (overlays.analysisReportFocus(); as f) {
      <div class="overlay" data-testid="analysis-report-overlay" (click)="overlays.closeAnalysisReport()">
        <div class="overlay__panel" (click)="$event.stopPropagation()">
          <button class="overlay__close" (click)="overlays.closeAnalysisReport()" title="Close">×</button>
          <app-analysis-report-drilldown
            [projectName]="f.project"
            [reportId]="f.reportId"
            (close)="overlays.closeAnalysisReport()" />
        </div>
      </div>
    }
  `,
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

  /** The shell needs to provide watchPaths for hash → name resolution on rail change. */
  readonly railChangeNeedsWatchPaths = output<ProjectRailKey>();

  hasCustomPanel(rail: ProjectRailKey): boolean {
    return rail === 'security'
      || rail === 'uxui'
      || rail === 'token-usage'
      || rail === 'observability'
      || rail === 'product-runtime'
      || rail === 'steering';
  }

  setProjectShellRail(key: ProjectRailKey): void {
    this.overlays.setProjectShellRail(key);
  }
}
