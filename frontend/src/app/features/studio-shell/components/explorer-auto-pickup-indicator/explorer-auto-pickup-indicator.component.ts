import { ChangeDetectionStrategy, Component, ViewEncapsulation, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { ProjectAutoPickupState } from '../../studio-shell.auto-pickup';

/**
 * Static auto-pickup configuration mark for Explorer project rows and their
 * collapsed aggregates. Its fixed slot keeps labels, counts, and row height
 * stable across every state.
 */
@Component({
  selector: 'app-explorer-auto-pickup-indicator',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  encapsulation: ViewEncapsulation.None,
  templateUrl: './explorer-auto-pickup-indicator.component.html',
  styleUrl: './explorer-auto-pickup-indicator.component.scss',
})
export class ExplorerAutoPickupIndicatorComponent {
  readonly state = input<ProjectAutoPickupState | 'off'>('off');
  readonly tooltip = input('');
  readonly testid = input<string | null>(null);
  readonly aggregate = input(false);
  readonly reason = input<string | null>(null);
}
