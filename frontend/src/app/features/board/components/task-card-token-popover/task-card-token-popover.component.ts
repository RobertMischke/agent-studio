import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { formatTokens, type TaskTokenBubble } from '../task-card/task-card-view-model';

@Component({
  selector: 'app-task-card-token-popover',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-card-token-popover.component.html',
  styleUrl: './task-card-token-popover.component.scss',
})
export class TaskCardTokenPopoverComponent {
  readonly bubble = input.required<TaskTokenBubble>();

  formatTokens(value: number): string {
    return formatTokens(value);
  }
}
