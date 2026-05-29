import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TooltipDirective } from '../../../../components/tooltip/tooltip.directive';

export type StudioActivityPanelKey = 'explorer' | 'filters' | 'cli' | 'activity' | 'runbook';

export interface StudioActivityBarItem {
  key: StudioActivityPanelKey;
  icon: 'folder' | 'filter' | 'cli' | 'activity' | 'runbook';
  label: string;
}

@Component({
  selector: 'app-studio-activity-bar',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './studio-activity-bar.component.html',
  styleUrl: './studio-activity-bar.component.scss',
})
export class StudioActivityBarComponent {
  readonly items = input.required<readonly StudioActivityBarItem[]>();
  readonly activePanel = input.required<string>();
  readonly sidebarVisible = input.required<boolean>();
  /** Per-item badge counts. Items with count > 0 show a small badge on the icon. */
  readonly badgeCounts = input<Readonly<Record<string, number>>>({});
  readonly panelToggle = output<StudioActivityPanelKey | 'settings'>();
}
