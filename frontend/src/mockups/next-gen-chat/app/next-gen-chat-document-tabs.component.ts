import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';

import { ICON_PATHS } from './next-gen-chat-workbench-prototype.data';
import { WorkbenchDocument, WorkbenchDocumentId } from './next-gen-chat-workbench-prototype.models';

@Component({
  selector: 'mockup-next-gen-chat-document-tabs',
  standalone: true,
  templateUrl: './next-gen-chat-document-tabs.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NextGenChatDocumentTabsComponent {
  readonly documents = input.required<readonly WorkbenchDocument[]>();
  readonly activeDocument = input.required<WorkbenchDocumentId>();

  readonly documentActivated = output<WorkbenchDocumentId>();
  readonly documentClosed = output<WorkbenchDocumentId>();

  iconPath(name: string): string[] {
    return ICON_PATHS[name] ?? ICON_PATHS['help'];
  }
}
