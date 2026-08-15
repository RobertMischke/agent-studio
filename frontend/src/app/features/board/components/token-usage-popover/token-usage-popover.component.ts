import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { formatTokens, type TaskTokenBubble } from '../task-card/task-card-view-model';

@Component({
  selector: 'app-token-usage-popover',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-usage-popover.component.html',
  styleUrl: './token-usage-popover.component.scss',
})
export class TokenUsagePopoverComponent {
  readonly bubble = input.required<TaskTokenBubble>();
  protected readonly formatTokens = formatTokens;
}
