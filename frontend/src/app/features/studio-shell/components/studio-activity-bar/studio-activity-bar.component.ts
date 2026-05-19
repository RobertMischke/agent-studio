import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

export type StudioActivityPanelKey = 'explorer' | 'tasks' | 'filters' | 'cli' | 'activity' | 'runbook';

export interface StudioActivityBarItem {
  key: StudioActivityPanelKey;
  icon: 'folder' | 'list' | 'filter' | 'cli' | 'activity' | 'runbook';
  label: string;
}

@Component({
  selector: 'app-studio-activity-bar',
  standalone: true,
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './studio-activity-bar.component.html',
  styleUrl: './studio-activity-bar.component.scss',
})
export class StudioActivityBarComponent {
  readonly items = input.required<readonly StudioActivityBarItem[]>();
  readonly activePanel = input.required<string>();
  readonly sidebarVisible = input.required<boolean>();
  readonly panelToggle = output<StudioActivityPanelKey | 'settings'>();
}
