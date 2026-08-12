import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { formatTokens, type TaskTokenBubble } from '../task-card/task-card-view-model';

@Component({
  selector: 'app-task-token-usage-popover',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './token-usage-popover.component.html',
  styleUrl: './token-usage-popover.component.scss',
})
export class TaskTokenUsagePopoverComponent {
  readonly usage = input.required<TaskTokenBubble>();

  formatTokens(value: number): string {
    return formatTokens(value);
  }
}
