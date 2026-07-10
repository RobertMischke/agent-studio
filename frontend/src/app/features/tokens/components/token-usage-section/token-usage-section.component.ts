import { ChangeDetectionStrategy, Component, OnDestroy, OnInit, inject, output } from '@angular/core';
import type { CliType } from '../../../../models/task.model';
import { CliUsageStore } from '../../services/cli-usage.store';
import { CliUsageDetailComponent } from '../cli-usage-detail/cli-usage-detail';
import { WorkspaceTokenTimelineComponent } from '../workspace-token-timeline/workspace-token-timeline';

/**
 * AGT-2035 — the single "Token usage" area of the consolidated Workspace
 * settings.
 *
 * The rich usage displays (quota / token trend / model spend / expensive
 * tasks) used to be duplicated inside the "Usage caps" (CLI Management)
 * section. Per the operator direction there must be **one usage area, no
 * double display**: those displays were moved here, next to the workspace
 * token timeline, so Token usage is now the one place that answers "where did
 * the tokens go". Usage caps keeps only the caps sliders.
 *
 * This host owns the ref-counted {@link CliUsageStore} lifecycle so the heavy
 * token / timeline polls run only while this section is mounted. Data
 * correctness of the aggregate is AGT-2038's remit; this card is structure.
 */
@Component({
  selector: 'app-token-usage-section',
  standalone: true,
  imports: [WorkspaceTokenTimelineComponent, CliUsageDetailComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-usage-section.component.html',
  styleUrl: './token-usage-section.component.scss',
})
export class TokenUsageSectionComponent implements OnInit, OnDestroy {
  readonly usage = inject(CliUsageStore);

  /** Bubbled up (through WorkspaceOverlaysComponent) so the shell can route to a
   *  project's Settings when a "By project" usage row is clicked. */
  readonly openProjectSettings = output<string>();

  ngOnInit(): void {
    this.usage.startDetail();
  }

  ngOnDestroy(): void {
    this.usage.stopDetail();
  }

  refreshOne(event: { cliType: CliType; event: Event }): void {
    this.usage.refreshOne(event.cliType);
  }
}
