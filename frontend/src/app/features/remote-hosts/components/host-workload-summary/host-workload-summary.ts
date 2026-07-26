import { ChangeDetectionStrategy, Component, input } from '@angular/core';

@Component({
  selector: 'app-host-workload-summary',
  standalone: true,
  templateUrl: './host-workload-summary.html',
  styleUrl: './host-workload-summary.scss',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class HostWorkloadSummaryComponent {
  readonly runSlots = input.required<string>();
  readonly gateWork = input.required<string>();
}
