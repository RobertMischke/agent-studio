import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { TooltipDirective } from '../../../../components/tooltip/tooltip.directive';
import { StudioIconComponent } from '../../../../components/studio-icon/studio-icon.component';

export type StudioActivityPanelKey = 'explorer' | 'filters' | 'cli' | 'activity' | 'runbook';

export interface StudioActivityBarItem {
  key: StudioActivityPanelKey;
  icon: 'folder' | 'filter' | 'cli' | 'activity' | 'runbook';
  label: string;
}

@Component({
  selector: 'app-studio-activity-bar',
  standalone: true,
  imports: [TooltipDirective, StudioIconComponent],
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
  /** True while the backlog triage screen is visible. Drives the active highlight. */
  readonly backlogActive = input<boolean>(false);
  /** Count of `0-backlog` tasks under the current project filter; renders a numeric badge. */
  readonly backlogCount = input<number>(0);
  /** True while the epic overview screen is visible. Drives the active highlight. */
  readonly epicsActive = input<boolean>(false);
  /** Whether any epics exist. The Epics button is hidden when false. */
  readonly hasEpics = input<boolean>(false);
  readonly panelToggle = output<StudioActivityPanelKey | 'settings'>();
  /** Fires when the user clicks the always-visible Backlog button. */
  readonly openBacklogRequest = output<void>();
  /** Fires when the user clicks the Epics button (shown only when epics exist). */
  readonly openEpicsRequest = output<void>();
}
