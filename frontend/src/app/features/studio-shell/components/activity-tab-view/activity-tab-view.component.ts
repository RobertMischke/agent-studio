import { ChangeDetectionStrategy, Component, computed, inject, input } from '@angular/core';
import { JobService } from '../../../../services/task.service';
import { RowComponent } from '../../../../components/row/row.component';
import type { JobInfo } from '../../../../models/task.model';

/**
 * Full-screen "Activity" tab. Looks up the owning job by jobKey and
 * renders the live execution + run-outcome summary, plus a CTA back to
 * the in-task chat workbench (which still owns the streaming protocol
 * pane). The inline activity log streaming is a follow-up.
 */
@Component({
  selector: 'app-studio-activity-view',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RowComponent],
  templateUrl: './activity-tab-view.component.html',
  styleUrl: './activity-tab-view.component.scss',
})
export class StudioActivityViewComponent {
  private readonly jobService = inject(JobService);

  readonly jobKey = input.required<string>();

  readonly job = computed<JobInfo | null>(() => {
    const key = this.jobKey();
    return this.jobService.jobs().find(j => j.jobKey === key) ?? null;
  });
}
