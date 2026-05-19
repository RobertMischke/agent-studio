import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { ICON_PATHS } from './next-gen-chat-workbench-prototype.data';
import {
  PaneButton,
  Scenario,
  ScenarioOption,
  SummaryChip,
  WorkbenchPane,
} from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'mockup-next-gen-chat-rail',
  standalone: true,
  templateUrl: './next-gen-chat-rail.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NextGenChatRailComponent {
  readonly paneButtons = input.required<readonly PaneButton[]>();
  readonly scenarios = input.required<readonly ScenarioOption[]>();
  readonly activeScenario = input.required<Scenario>();
  readonly summaryChips = input.required<readonly SummaryChip[]>();
  readonly activePanes = input.required<readonly WorkbenchPane[]>();
  readonly openCount = input.required<number>();

  readonly guideRequested = output<void>();
  readonly allDocumentsRequested = output<void>();
  readonly paneSelected = output<WorkbenchPane>();
  readonly scenarioSelected = output<Scenario>();
  readonly summaryPaneSelected = output<WorkbenchPane>();

  isPaneActive(pane: WorkbenchPane): boolean {
    return this.activePanes().includes(pane);
  }

  iconPath(name: string): string[] {
    return ICON_PATHS[name] ?? ICON_PATHS['help'];
  }
}
