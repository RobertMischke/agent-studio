import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, output } from '@angular/core';
import { WorkspaceOverlaysService } from '../../state/workspace-overlays.service';
import { WorkspaceTokenTimelineComponent } from '../../../tokens/components/workspace-token-timeline';
import { WorkspaceScreenshotsComponent } from '../../../screenshots/components/workspace-screenshots';
import { CliAdminPanelComponent } from '../../../cli';
import type { JobScreenshot } from '../../../../features/screenshots';
import { ModalStackService } from '../../../../services/modal-stack.service';

import { TooltipDirective } from '../../../../components/tooltip';
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
  imports: [WorkspaceTokenTimelineComponent, WorkspaceScreenshotsComponent, CliAdminPanelComponent, TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './workspace-overlays.component.html',
})
export class WorkspaceOverlaysComponent {
  readonly overlays = inject(WorkspaceOverlaysService);
  readonly openTask = output<JobScreenshot>();

  private readonly modalStack = inject(ModalStackService);
  private readonly destroyRef = inject(DestroyRef);
  private disposers = new Map<string, () => void>();

  constructor() {
    this.bind('workspace-tokens', this.overlays.tokensOpen, () => this.overlays.closeTokens());
    this.bind('workspace-screenshots', this.overlays.screenshotsOpen, () => this.overlays.closeScreenshots());
    this.bind('cli-admin', this.overlays.cliAdminOpen, () => this.overlays.closeCliAdmin());
    this.destroyRef.onDestroy(() => {
      for (const d of this.disposers.values()) d();
      this.disposers.clear();
    });
  }

  private bind(id: string, open: () => boolean, close: () => void): void {
    effect(() => {
      const isOpen = open();
      const existing = this.disposers.get(id);
      if (isOpen && !existing) {
        this.disposers.set(id, this.modalStack.push(id, close));
      } else if (!isOpen && existing) {
        existing();
        this.disposers.delete(id);
      }
    });
  }
}
