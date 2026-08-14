import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { formatTokens, type TaskTokenBubble } from '../task-card/task-card-view-model';

@Component({
  selector: 'app-task-token-popover',
  standalone: true,
  imports: [TooltipDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-token-popover.html',
  styleUrl: './task-token-popover.scss',
})
export class TaskTokenPopoverComponent {
  readonly value = input.required<TaskTokenBubble>();
  readonly formatTokens = formatTokens;
}
