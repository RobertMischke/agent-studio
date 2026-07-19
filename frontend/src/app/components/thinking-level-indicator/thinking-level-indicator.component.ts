import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import type { ThinkingLevelIndicator } from '../../services/thinking-level.util';

@Component({
  selector: 'app-thinking-level-indicator',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './thinking-level-indicator.component.html',
  styleUrl: './thinking-level-indicator.component.scss',
})
export class ThinkingLevelIndicatorComponent {
  readonly indicator = input.required<ThinkingLevelIndicator>();
  readonly testId = input('thinking-level-indicator');
}
