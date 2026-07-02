import {
  ChangeDetectionStrategy,
  Component,
  HostListener,
  output,
  signal,
} from '@angular/core';
import { OrchestratorLogicPanelComponent } from '../orchestrator-logic-panel/orchestrator-logic-panel.component';
import { OverlayPortalDirective } from '../../../../directives/overlay-portal.directive';
import { TooltipDirective } from '@coding-agent/chat/shared';

type SettingsRailKey = 'orchestrator' | 'general';

interface SettingsRailItem {
  key: SettingsRailKey;
  group: 'configuration';
  label: string;
  panelTitle: string;
  description: string;
  icon: string;
}

const RAIL_ITEMS: readonly SettingsRailItem[] = [
  {
    key: 'orchestrator',
    group: 'configuration',
    label: 'Orchestrator',
    panelTitle: 'Orchestrator',
    description: 'Runtime flags for orchestrator, supervisor, meta-cycle, and auto-intervention loops.',
    icon: '\u{1F916}',
  },
  {
    key: 'general',
    group: 'configuration',
    label: 'General',
    panelTitle: 'General',
    description: 'App-wide preferences. Future home for theme, sound, and editor defaults.',
    icon: '⚙',
  },
];

@Component({
  selector: 'app-orchestrator-settings-modal',
  standalone: true,
  imports: [OrchestratorLogicPanelComponent, TooltipDirective, OverlayPortalDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './orchestrator-settings-modal.component.html',
  styleUrl: './orchestrator-settings-modal.component.scss',
})
export class OrchestratorSettingsModalComponent {
  readonly closed = output<void>();

  readonly activeKey = signal<SettingsRailKey>('orchestrator');
  readonly railItems = RAIL_ITEMS;

  activeItem(): SettingsRailItem {
    return RAIL_ITEMS.find(i => i.key === this.activeKey()) ?? RAIL_ITEMS[0];
  }

  selectRail(key: SettingsRailKey): void {
    if (key === this.activeKey()) return;
    this.activeKey.set(key);
  }

  close(): void {
    this.closed.emit();
  }

  onBackdropClick(event: Event): void {
    if (event.target === event.currentTarget) this.close();
  }

  @HostListener('document:keydown.escape')
  onEscape(): void {
    this.close();
  }
}
