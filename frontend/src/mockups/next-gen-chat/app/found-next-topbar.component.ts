import { Component, input, output } from '@angular/core';

import { ICON_PATHS, PROJECT_TABS, TOPBAR_RUN_STATS } from './next-gen-chat-workbench-prototype.data';
import { Density, StatusPanel, Theme } from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'app-found-next-topbar',
  standalone: true,
  templateUrl: './found-next-topbar.component.html',
  styleUrl: './found-next-topbar.component.scss',
})
export class FoundNextTopbarComponent {
  readonly theme = input.required<Theme>();
  readonly density = input.required<Density>();
  readonly sideSheetOpen = input.required<boolean>();
  readonly statusPanel = input<StatusPanel | null>(null);

  readonly projectPanelRequested = output<void>();
  readonly sideSheetToggled = output<void>();
  readonly queuePanelRequested = output<void>();
  readonly densityToggled = output<void>();
  readonly themeToggled = output<void>();
  readonly commandRequested = output<void>();
  readonly debugRequested = output<void>();
  readonly closeRequested = output<void>();

  readonly projectTabs = PROJECT_TABS;
  readonly runStats = TOPBAR_RUN_STATS;

  iconPath(name: string): string[] {
    return ICON_PATHS[name] ?? ICON_PATHS['help'];
  }
}
