import { ChangeDetectionStrategy, Component, inject, output } from '@angular/core';
import { WorkspaceOverlaysService } from '../state/workspace-overlays.service';
import { WorkspaceTokenTimelineComponent } from '../../tokens/components/workspace-token-timeline';
import { WorkspaceScreenshotsComponent } from '../../screenshots/components/workspace-screenshots';
import { CliAdminPanelComponent } from '../../cli/components/cli-admin-panel';
import type { JobScreenshot } from '../../../features/screenshots';

/**
 * Cycle 9g shell-feature container: renders the three workspace-level
 * overlays (tokens / screenshots / cli-admin) above the kanban shell.
 * State + URL-hash sync owned by WorkspaceOverlaysService; this
 * component is just a thin renderer.
 *
 * The screenshots overlay emits `openTask` (job picked from the reel)
 * up to the shell because navigating to a job is shell-coordinated:
 * the shell owns `selectedJob` and the URL update path.
 */
@Component({
  selector: 'app-workspace-overlays',
  standalone: true,
  imports: [WorkspaceTokenTimelineComponent, WorkspaceScreenshotsComponent, CliAdminPanelComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-overlays.component.html',
})
export class WorkspaceOverlaysComponent {
  readonly overlays = inject(WorkspaceOverlaysService);
  readonly openTask = output<JobScreenshot>();
}
