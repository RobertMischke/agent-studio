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
  template: `
    @if (overlays.tokensOpen()) {
      <div class="overlay" data-testid="workspace-tokens-overlay" (click)="overlays.closeTokens()">
        <div class="overlay__panel overlay__panel--wtt" (click)="$event.stopPropagation()">
          <button class="overlay__close" (click)="overlays.closeTokens()" title="Close">×</button>
          <app-workspace-token-timeline />
        </div>
      </div>
    }

    @if (overlays.screenshotsOpen()) {
      <div class="overlay" data-testid="workspace-screenshots-overlay" (click)="overlays.closeScreenshots()">
        <div class="overlay__panel overlay__panel--wtt" (click)="$event.stopPropagation()">
          <button class="overlay__close" (click)="overlays.closeScreenshots()" title="Close">×</button>
          <app-workspace-screenshots (openTask)="openTask.emit($event)" />
        </div>
      </div>
    }

    @if (overlays.cliAdminOpen()) {
      <div class="overlay" data-testid="cli-admin-overlay" (click)="overlays.closeCliAdmin()">
        <div class="overlay__panel" (click)="$event.stopPropagation()">
          <button class="overlay__close" (click)="overlays.closeCliAdmin()" title="Close">×</button>
          @defer {
            <app-cli-admin-panel />
          }
        </div>
      </div>
    }
  `,
})
export class WorkspaceOverlaysComponent {
  readonly overlays = inject(WorkspaceOverlaysService);
  readonly openTask = output<JobScreenshot>();
}
