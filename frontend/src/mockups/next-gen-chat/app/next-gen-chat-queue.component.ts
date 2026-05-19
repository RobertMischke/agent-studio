import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { ICON_PATHS } from './next-gen-chat-workbench-prototype.data';
import { TaskQueueCard } from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'mockup-next-gen-chat-queue',
  standalone: true,
  templateUrl: './next-gen-chat-queue.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NextGenChatQueueComponent {
  readonly tasks = input.required<readonly TaskQueueCard[]>();
  readonly closeRequested = output<void>();

  iconPath(name: string): string[] {
    return ICON_PATHS[name] ?? ICON_PATHS['help'];
  }
}
