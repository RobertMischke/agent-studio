import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { ICON_PATHS } from './next-gen-chat-workbench-prototype.data';
import { ActivityItem, ActivityTarget } from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'mockup-next-gen-chat-activity-bar',
  standalone: true,
  templateUrl: './next-gen-chat-activity-bar.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NextGenChatActivityBarComponent {
  readonly items = input.required<readonly ActivityItem[]>();
  readonly activeActivity = input.required<ActivityTarget>();

  readonly activitySelected = output<ActivityTarget>();
  readonly closeRequested = output<void>();

  iconPath(name: string): string[] {
    return ICON_PATHS[name] ?? ICON_PATHS['help'];
  }
}
