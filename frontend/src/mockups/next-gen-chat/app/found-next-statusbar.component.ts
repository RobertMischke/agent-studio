import { Component, output } from '@angular/core';

import { USAGE_STRIP } from './next-gen-chat-workbench-prototype.data';
import { StatusPanel } from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'app-found-next-statusbar',
  standalone: true,
  templateUrl: './found-next-statusbar.component.html',
  styleUrl: './found-next-statusbar.component.scss',
})
export class FoundNextStatusbarComponent {
  readonly statusPanelRequested = output<StatusPanel>();
  readonly sideSheetRequested = output<void>();
  readonly debugTraceRequested = output<void>();

  readonly usageStrip = USAGE_STRIP;
  readonly compactUsageSummary = USAGE_STRIP
    .filter((item) => item.window === '5h')
    .map((item) => `${item.label === 'Codex' ? 'Cdx' : item.label.slice(0, 2)} ${item.value}`)
    .join(' / ');
}
