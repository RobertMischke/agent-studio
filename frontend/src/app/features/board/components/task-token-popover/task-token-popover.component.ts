import { ChangeDetectionStrategy, Component, input } from '@angular/core';
import { TooltipDirective } from 'coding-agent-chat/shared';
import { TokenPopoverDirective } from '../task-card/token-popover.directive';
import { formatTokens, type TaskTokenBubble } from '../task-card/task-card-view-model';

@Component({
  selector: 'app-task-token-popover',
  standalone: true,
  imports: [TooltipDirective, TokenPopoverDirective],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './task-token-popover.component.html',
  styleUrl: './task-token-popover.component.scss',
})
export class TaskTokenPopoverComponent {
  readonly bubble = input.required<TaskTokenBubble>();
  readonly formatTokens = formatTokens;
}
