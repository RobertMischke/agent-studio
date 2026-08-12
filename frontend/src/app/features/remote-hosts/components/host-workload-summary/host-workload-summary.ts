import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';
import { AppTooltipDirective } from '../../../../components/tooltip/app-tooltip.directive';

@Component({
  selector: 'app-host-workload-summary',
  standalone: true,
  imports: [StudioIconComponent, AppTooltipDirective],
  templateUrl: './host-workload-summary.html',
  styleUrl: './host-workload-summary.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HostWorkloadSummaryComponent {
  readonly codingSlots = input.required<string>();
  readonly codingSlotsStale = input(false);
  readonly codingSlotsDiverge = input(false);
  readonly codingSlotsTooltip = input('');
  readonly reviewSlots = input.required<string>();
  readonly reviewSlotsStale = input(false);
  readonly gateWork = input.required<string>();
}
