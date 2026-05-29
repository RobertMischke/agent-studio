import { ChangeDetectionStrategy, Component, computed, effect, inject, input, output, signal } from '@angular/core';
import { TaskService } from '../../../../services/task.service';
import type { TaskInfo } from '../../../../models/task.model';
import {
  ProjectShellComponent,
  ProjectDetailComponent,
  SecurityPanelComponent,
  UxuiPanelComponent,
  ProjectObservabilityPanelComponent,
  ProjectProductRuntimePanelComponent,
  ProjectSteeringDocsSectionComponent,
  ProjectSkillReadinessSectionComponent,
} from '../../../project-detail';
import { ProjectTokenUsagePanelComponent } from '../../../project-token-usage';
import { WorkspaceScreenshotsComponent } from '../../../screenshots';
import {
  DEFAULT_PROJECT_RAIL_KEY,
  ProjectRailKey,
  isProjectRailKey,
} from '../../../project-detail/components/project-shell/project-shell.config';
import { ProjectOverlaysService } from '../../../project-detail/state/project-overlays.service';

/** Rails whose content panel is real (not the project-shell placeholder). */
const RAILS_WITH_CUSTOM_PANEL: ReadonlySet<ProjectRailKey> = new Set<ProjectRailKey>([
  'overview',
  'visual-evidence',
  'security',
  'architecture',
  'drift',
  'uxui',
  'token-usage',
  'observability',
  'product-runtime',
  'steering',
  'jobs',
  'settings',
  'orchestrator',
  'activity',
]);

/**
 * Project Hub tab — the per-project landing surface inside the studio
 * editor. Embeds the legacy <app-project-shell> directly so the full
 * project navigation (Overview / Visual Evidence / Security /
 * Architecture / Drift / UX-UI / Test Quality / Token Usage /
 * Observability / Product Runtime / Steering / Audits / Jobs /
 * Settings / Orchestrator / Activity) is reachable from the tab —
 * no need to open a separate overlay, and every rail uses its real
 * content panel where one exists.
 */
@Component({
  selector: 'app-project-hub-view',
  standalone: true,
  imports: [
    ProjectShellComponent,
    ProjectDetailComponent,
    SecurityPanelComponent,
    UxuiPanelComponent,
    ProjectTokenUsagePanelComponent,
    ProjectObservabilityPanelComponent,
    ProjectProductRuntimePanelComponent,
    ProjectSteeringDocsSectionComponent,
    ProjectSkillReadinessSectionComponent,
    WorkspaceScreenshotsComponent,
  ],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './project-hub-view.component.html',
  styleUrl: './project-hub-view.component.scss',
})
export class ProjectHubViewComponent {
  private readonly jobService = inject(TaskService);
  private readonly overlays = inject(ProjectOverlaysService);

  readonly projectName = input.required<string>();
  /** Optional initial rail; defaults to "overview" if absent or unknown. */
  readonly initialSection = input<string>('overview');

  /** Bubbles to the parent so it can navigate when a row link is clicked. */
  readonly openTask = output<{ jobId: string; watchPath: string }>();

  readonly activeRail = signal<ProjectRailKey>(DEFAULT_PROJECT_RAIL_KEY);

  constructor() {
    effect(() => {
      const raw = this.initialSection();
      this.activeRail.set(isProjectRailKey(raw) ? raw : DEFAULT_PROJECT_RAIL_KEY);
    });
  }

  readonly jobsForProject = computed<TaskInfo[]>(() => {
    const grouped = this.jobService.grouped();
    const out: TaskInfo[] = [];
    for (const lane of Object.values(grouped)) {
      for (const job of lane as TaskInfo[]) {
        if (job.projectName === this.projectName()) out.push(job);
      }
    }
    return out;
  });

  hasCustomPanel(rail: ProjectRailKey): boolean {
    return RAILS_WITH_CUSTOM_PANEL.has(rail);
  }

  setRail(rail: ProjectRailKey): void {
    this.activeRail.set(rail);
  }

  /**
   * Open the orchestrator feed overlay (same target as the legacy
   * project-shell's 📜 Open feed button).
   */
  openFeed(): void {
    this.overlays.openOrchFeed(this.projectName());
  }

  openFeedFromDetail(intent: string): void {
    void intent;
    this.overlays.openOrchFeed(this.projectName());
  }

  openReport(report: { reportId: string }): void {
    this.overlays.openAnalysisReport(this.projectName(), report.reportId);
  }

  /** Hub closes when the user closes the tab; no separate close action. */
  closeShell(): void {
    /* no-op — the tab close button handles this */
  }

  onSecurityFollowUp(evt: unknown): void { void evt; }
  onSecurityOpenEvidence(evt: unknown): void { void evt; }
  onSecurityAuditQueued(evt: unknown): void { void evt; }
  onUxuiFollowUp(evt: unknown): void { void evt; }
  onUxuiActionQueued(evt: unknown): void { void evt; }
}
