import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, computed, inject, input } from '@angular/core';
import { NotificationComponent } from '../../../../components/notification/notification.component';
import {
  RemoteQueueStarvationService,
  type BuildProfileGateBlockage,
} from '../../services/remote-queue-starvation.service';

/**
 * Loud, persistent alarm for the failure mode behind the 2026-08-18 Quality
 * Studio outage (AGT-2677): a project's build profile is not validated, so the
 * pickup gate refuses every one of its ready cards. The cards themselves look
 * ordinary - "queued-remote", no error - which is exactly why this needs to be a
 * banner and not a per-card hint. It names the project, the number of cards it
 * is holding, and the gate's own reason, so the operator reaches the one action
 * that helps instead of restarting runners.
 */
@Component({
  selector: 'app-build-profile-gate-banner',
  standalone: true,
  imports: [NotificationComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './build-profile-gate-banner.html',
  styleUrl: './build-profile-gate-banner.scss',
})
export class BuildProfileGateBannerComponent implements OnInit, OnDestroy {
  private readonly starvation = inject(RemoteQueueStarvationService);
  private detach: (() => void) | null = null;

  /** Projects currently in view; empty means "no filter". */
  readonly projects = input<readonly string[]>([]);

  readonly blockages = computed<readonly BuildProfileGateBlockage[]>(() => {
    const all = this.starvation.snapshot()?.gateBlockedProjects ?? [];
    const projects = this.projects();
    if (projects.length === 0) return all;
    const visible = new Set(projects.map(project => project.toLowerCase()));
    return all.filter(blockage => visible.has(blockage.projectName.toLowerCase()));
  });

  readonly blockedTaskCount = computed(() =>
    this.blockages().reduce((sum, blockage) => sum + blockage.readyTaskCount, 0));

  /** "QualityStudio (25)" per project, so a multi-project outage stays readable. */
  readonly projectSummary = computed(() => this.blockages()
    .map(blockage => `${blockage.projectName} (${blockage.readyTaskCount})`)
    .join(', '));

  /**
   * The gate's own reason, shown verbatim when a single project is affected.
   * With several projects the reasons differ, so the summary above carries the
   * detail and the operator opens the project to see its specific gate.
   */
  readonly singleReason = computed(() => {
    const blockages = this.blockages();
    return blockages.length === 1 ? blockages[0].gateReason : null;
  });

  ngOnInit(): void {
    this.detach = this.starvation.attach();
  }

  ngOnDestroy(): void {
    this.detach?.();
    this.detach = null;
  }
}
